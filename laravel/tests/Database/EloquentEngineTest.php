<?php

use Illuminate\Support\Facades\DB;
use Zouriel\Workflows\Core\Changes\FieldOwnedException;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Laravel\Registry\ModelRegistry;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;
use Zouriel\Workflows\Tests\Support\App\Application;
use Zouriel\Workflows\Tests\Support\App\ApplicationStatus;
use Zouriel\Workflows\Tests\Support\App\Document;
use Zouriel\Workflows\Tests\Support\App\Programme;
use Zouriel\Workflows\Tests\Support\App\Unwatched;
use Zouriel\Workflows\Tests\Support\App\UrgentApplication;
use Zouriel\Workflows\Tests\Support\EloquentTestCase;

uses(EloquentTestCase::class);

// Ported from .NET EfEngineTests / StoreBehaviourTests: the engine on the database store, with the app's records
// written through Eloquent models and seen through HasWorkflows.

const ON_CHECKED = <<<'JSON'
{ "specVersion": 1, "id": "wf_chk", "key": "onChecked", "kind": "automation", "title": "On checked", "entity": "application",
  "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
  "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "notes", "value": { "calc": { "op": "concat", "args": [ { "const": "Checked" }, { "ref": "entity.title" } ] } } } ] } ] }
JSON;

const REVIEW = <<<'JSON'
{ "specVersion": 1, "id": "lc", "key": "review", "kind": "lifecycle", "title": "Review", "entity": "application",
  "lifecycle": { "field": "status", "states": [
      { "id": "d", "key": "Draft", "label": "Draft", "category": "initial" },
      { "id": "s", "key": "Submitted", "label": "Submitted", "category": "active" },
      { "id": "a", "key": "Approved", "label": "Approved", "category": "terminal" } ],
    "transitions": [
      { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "field": "entity.applicant" } ],
        "steps": [ { "id": "x", "type": "setFields", "values": [ { "field": "stage", "value": { "const": "checked" } } ] } ] },
      { "id": "t2", "key": "approve", "label": "Approve", "from": ["s"], "to": "a", "who": [ { "role": "reviewer" } ] } ] } }
JSON;

function application(array $values = []): Application
{
    return Application::query()->create(['title' => 'Bursary', 'stage' => 'new', ...$values]);
}

it('starts a workflow for a committed change and saves its write through the model', function () {
    $this->publish(ON_CHECKED);
    $a = application();
    $a->update(['stage' => 'checked']);
    $this->settle();

    expect($a->fresh()->notes)->toBe('Checked Bursary')
        ->and($this->engine()->queryInstances(new InstanceQuery(definitionKey: 'onChecked'))->items)->toHaveCount(1)
        ->and($this->engine()->queryInstances(new InstanceQuery(definitionKey: 'onChecked'))->items[0]->status)->toBe('completed');
});

it('writes the change event in the same transaction as the record, so a rolled back change starts nothing', function () {
    $this->publish(ON_CHECKED);
    $a = application();
    $this->settle();
    $before = DB::table('wf_outbox')->count();

    DB::beginTransaction();
    $a->update(['stage' => 'checked']);
    expect(DB::table('wf_outbox')->count())->toBe($before + 1);
    DB::rollBack();
    $this->settle();

    expect(DB::table('wf_outbox')->count())->toBe($before)
        ->and($a->fresh()->stage)->toBe('new')
        ->and($a->fresh()->notes)->toBeNull()
        ->and($this->engine()->queryInstances(new InstanceQuery(definitionKey: 'onChecked'))->items)->toBe([]);
});

it('opens a transaction around a save when none is open, so the record and its event commit together', function () {
    $this->publish(ON_CHECKED);
    $levels = [];
    Application::updating(function () use (&$levels) {
        $levels[] = DB::transactionLevel();
    });
    application()->update(['stage' => 'checked']);

    expect($levels)->toBe([1])
        ->and(DB::table('wf_outbox')->where('record_key', 'like', 'application:%')->count())->toBe(2);
});

it('fills the initial state on create, refuses outside writes to the owned field and applies transitions', function () {
    $this->publish(REVIEW);
    $a = application(['applicant_id' => 'ali']);
    expect($a->status)->toBe(ApplicationStatus::Draft)
        ->and($a->fresh()->status)->toBe(ApplicationStatus::Draft);
    $this->settle();

    expect(fn () => $a->fresh()->update(['status' => ApplicationStatus::Approved]))->toThrow(FieldOwnedException::class);
    expect($a->fresh()->status)->toBe(ApplicationStatus::Draft);
    // The refusal is audited in a unit of its own once the refused write rolled back.
    $refused = DB::table('wf_audit')->where('category', 'lifecycle')->where('action', 'writeRefused')->first();
    expect($refused)->not->toBeNull()
        ->and($refused->entity_id)->toBe((string) $a->id);

    $view = $this->engine()->applyTransition('application', (string) $a->id, 'submit', null, WorkflowActor::user('ali', 'Ali'));
    $this->settle();
    expect($view->state)->toBe('Submitted')
        ->and($a->fresh()->status)->toBe(ApplicationStatus::Submitted)
        ->and($a->fresh()->stage)->toBe('checked');
    // The engine's own writes come back as change events marked as its own, so the lifecycle does not react to them.
    $moved = DB::table('wf_outbox')->where('record_key', "application:{$a->id}")->where('kind', 'change')->pluck('payload')
        ->map(fn ($p) => json_decode($p))->first(fn ($e) => isset($e->fields->status) && $e->fields->status->to === 'Submitted');
    expect($moved->engineFields)->toBe(['status']);
});

it('sees the writes of a subclass of a registered model', function () {
    $this->publish(REVIEW);
    $urgent = UrgentApplication::query()->create(['title' => 'Urgent']);
    expect($urgent->status)->toBe(ApplicationStatus::Draft);
    expect(fn () => $urgent->update(['status' => ApplicationStatus::Approved]))->toThrow(FieldOwnedException::class);
});

it('refuses a model without HasWorkflows as an entity', function () {
    expect(fn () => (new ModelRegistry)->entity('ghost', Unwatched::class))
        ->toThrow(LogicException::class, 'does not use');
});

it('runs actions inside the unit: their model writes commit with the workflow, or are undone when they fail', function () {
    $this->publish(<<<'JSON'
    { "specVersion": 1, "id": "wf_act", "key": "act", "kind": "automation", "title": "Act", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
      "steps": [ { "id": "a", "type": "action", "action": "stamp", "input": { "text": { "const": "stamped" } } },
                 { "id": "b", "type": "setFields", "values": [ { "field": "amount", "value": { "ref": "steps.a.length" } } ] },
                 { "id": "c", "type": "action", "action": "stampThenFail" } ] }
    JSON);
    $a = application();
    $a->update(['stage' => 'checked']);
    $this->settle();

    $fresh = $a->fresh();
    expect($fresh->notes)->toBe('stamped')
        ->and((float) $fresh->amount)->toBe(7.0)
        ->and(Programme::query()->count())->toBe(0)
        ->and($this->engine()->queryInstances(new InstanceQuery(definitionKey: 'act'))->items[0]->status)->toBe('incident');
});

it('reads related records for forEach and writes each one back through its model', function () {
    $this->publish(<<<'JSON'
    { "specVersion": 1, "id": "wf_each", "key": "each", "kind": "automation", "title": "Each", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
      "steps": [ { "id": "loop", "type": "forEach", "collection": "entity.documents", "max": 10,
        "steps": [ { "id": "v", "type": "setFields", "target": "item", "values": [ { "field": "verified", "value": { "const": true } } ] } ] },
        { "id": "n", "type": "setFields", "values": [ { "field": "amount", "value": { "ref": "steps.loop.count" } } ] } ] }
    JSON);
    $a = application();
    $one = Document::query()->create(['application_id' => $a->id, 'name' => 'ID card']);
    $two = Document::query()->create(['application_id' => $a->id, 'name' => 'Transcript']);
    $a->update(['stage' => 'checked']);
    $this->settle();

    expect($one->fresh()->verified)->toBeTrue()
        ->and($two->fresh()->verified)->toBeTrue()
        ->and((float) $a->fresh()->amount)->toBe(2.0);
});

it('creates records through the model and reads relations, dates, decimals, enums and translations', function () {
    $this->publish(<<<'JSON'
    { "specVersion": 1, "id": "wf_new", "key": "newProgramme", "kind": "automation", "title": "New programme", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
      "steps": [ { "id": "p", "type": "createRecord", "entity": "programme", "values": [ { "field": "name", "value": { "const": { "en": "Science", "dv": "ސައިންސް" } } } ] },
                 { "id": "l", "type": "setFields", "values": [ { "field": "programme", "value": { "ref": "steps.p.id" } } ] } ] }
    JSON);
    $a = application(['amount' => '12.50', 'deadline' => '2026-12-01']);
    $a->update(['stage' => 'checked']);
    $this->settle();

    $programme = Programme::query()->sole();
    expect($programme->getTranslations('name'))->toBe(['en' => 'Science', 'dv' => 'ސައިންސް'])
        ->and($a->fresh()->programme_id)->toBe($programme->id);
    $values = DB::transaction(fn () => $this->app->make(DatabaseStore::class)->begin()->entities()->get('application', (string) $a->id)->values);
    expect($values->amount)->toBe(12.5)
        ->and($values->deadline)->toBe('2026-12-01')
        ->and($values->programme)->toBe((string) $programme->id)
        ->and($values->programmeName)->toEqual((object) ['en' => 'Science', 'dv' => 'ސައިންސް'])
        ->and($values->stage)->toBe('checked');
});

it('keeps one audit chain that verifies across the engine and the change hook', function () {
    $this->publish(REVIEW);
    $a = application(['applicant_id' => 'ali']);
    $this->settle();
    $this->engine()->applyTransition('application', (string) $a->id, 'submit', null, WorkflowActor::user('ali', 'Ali'));
    $this->settle();

    expect($this->engine()->verifyAudit()->valid)->toBeTrue()
        ->and(DB::table('wf_audit')->count())->toBeGreaterThan(3);
});
