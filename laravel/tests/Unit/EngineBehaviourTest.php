<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Engine\SimulationEvent;
use Zouriel\Workflows\Core\Engine\SimulationRequest;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\InvalidOperationException;
use Zouriel\Workflows\Core\Settings\GlobalSettings;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\WorkflowException;

if (! specAvailable()) {
    test('engine behaviour tests use the shared registry in spec/')->skip('spec/fixtures not found');

    return;
}

// Engine behaviours the shared fixtures do not reach (admin paths, limits, masking, people-only operations), ported
// from .NET EngineBehaviourTests, with the same expectations.

function behaviourAdmin(): WorkflowActor
{
    return WorkflowActor::user('admin', 'Admin', ['admin']);
}

function newRecord(): WorkflowTestHost
{
    return engineHost(['title' => 'Q', 'stage' => 'new']);
}

function changeStage(WorkflowTestHost $h, string $stage): void
{
    $h->store->changeRecord('application', 'a1', (object) ['stage' => $stage]);
    $h->settle();
}

function instanceByKey(WorkflowTestHost $h, string $key): InstanceRecord
{
    return array_values(array_filter($h->store->all('instances'), fn (InstanceRecord $i) => $i->definitionKey === $key))[0];
}

function saveSettings(WorkflowTestHost $h, string $json): void
{
    [, $revision] = $h->engine->getSettings();
    $h->engine->saveSettings(GlobalSettings::parse($json), $revision, behaviourAdmin());
}

function errorCode(callable $act): ?string
{
    try {
        $act();
    } catch (WorkflowException $e) {
        return $e->errorCode;
    }

    return null;
}

const TWO_FAILING_PATHS = <<<'JSON'
{ "specVersion": 1, "id": "wf_p", "key": "par", "kind": "automation", "title": "Par", "entity": "application",
  "trigger": { "type": "fieldChanged", "field": "stage" },
  "steps": [ { "id": "p", "type": "parallel", "join": { "type": "all" }, "paths": [
     { "id": "a", "label": "A", "steps": [ { "id": "x1", "type": "action", "action": "crm.ping", "input": {} } ] },
     { "id": "b", "label": "B", "steps": [ { "id": "x2", "type": "action", "action": "crm.ping", "input": {} } ] } ] } ] }
JSON;

const ALL_APPROVAL = <<<'JSON'
{ "specVersion": 1, "id": "wf_a", "key": "appr", "kind": "automation", "title": "Appr", "entity": "application",
  "trigger": { "type": "fieldChanged", "field": "stage" },
  "steps": [ { "id": "t", "type": "approval", "assignees": [ { "user": "rev1" }, { "user": "rev2" } ], "rule": { "type": "all" }, "due": "P3D",
               "allowDelegation": true, "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
JSON;

const PEOPLE_LIFECYCLE = <<<'JSON'
{ "specVersion": 1, "id": "lc_p", "key": "lcp", "kind": "lifecycle", "title": "LC", "entity": "application",
  "lifecycle": { "field": "status", "mode": "enforce",
    "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
    "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                     { "id": "t2", "key": "approve", "label": "Approve", "from": ["s"], "to": "o", "who": [ { "role": "reviewer" } ], "hidden": true },
                     { "id": "t3", "key": "expire", "label": "Expire", "from": ["d"], "to": "o" } ] } }
JSON;

/** @return array{0: WorkflowTestHost, 1: string} */
function openApproval(): array
{
    $h = newRecord();
    $h->publish(ALL_APPROVAL);
    changeStage($h, 'checked');

    return [$h, $h->store->all('tasks')[0]->id];
}

// ── Incidents ─────────────────────────────────────────────────────────────

it('skips the chosen incident and shows the other', function () {
    $h = newRecord();
    $h->actions->set('crm.ping', failTimes: 100, failMessage: 'CRM down');
    $h->publish(TWO_FAILING_PATHS);
    changeStage($h, 'checked');
    $id = instanceByKey($h, 'par')->id;

    $view = $h->engine->skip($id, (object) ['ok' => true], behaviourAdmin(), 'x2');
    expect($view->status)->toBe(InstanceStatus::INCIDENT)
        ->and($view->errorStepId)->toBe('x1')
        ->and($view->outputs->x2->ok)->toBeTrue()
        ->and($h->engine->skip($id, null, behaviourAdmin())->status)->toBe(InstanceStatus::COMPLETED);
});

it('refuses a retry of an incident that does not exist', function () {
    $h = newRecord();
    $h->actions->set('crm.ping', failTimes: 100);
    $h->publish(TWO_FAILING_PATHS);
    changeStage($h, 'checked');

    expect(errorCode(fn () => $h->engine->retry(instanceByKey($h, 'par')->id, behaviourAdmin(), 'nope')))->toBe('notInIncident')
        ->and($h->engine->retry(instanceByKey($h, 'par')->id, behaviourAdmin(), 'x1')->status)->toBe(InstanceStatus::INCIDENT);
});

it('gives each instance its own step budget and stops a run that never waits', function () {
    $h = engineHost();
    $h->engine->options->maxStepsPerRun = 4;
    $definition = fn (string $id, int $steps) => Json::encode((object) [
        'specVersion' => 1, 'id' => $id, 'key' => $id, 'kind' => 'automation', 'title' => $id,
        'variables' => [(object) ['id' => 'v', 'key' => 'v', 'type' => 'number']],
        'trigger' => (object) ['type' => 'signal', 'signal' => 'nightly'],
        'steps' => array_map(fn ($n) => (object) ['id' => "s{$n}", 'type' => 'setVariables', 'values' => [(object) ['variable' => 'v', 'value' => (object) ['const' => $n]]]],
            range(1, $steps)),
    ]);
    $h->publish($definition('one', 3));
    $h->publish($definition('two', 3));
    $h->publish($definition('long', 6));
    $h->engine->signal('nightly', null, null, null);

    expect(instanceByKey($h, 'one')->status)->toBe(InstanceStatus::COMPLETED)
        ->and(instanceByKey($h, 'two')->status)->toBe(InstanceStatus::COMPLETED)
        ->and(instanceByKey($h, 'long')->status)->toBe(InstanceStatus::INCIDENT)
        ->and(instanceByKey($h, 'long')->error)->toContain('too many steps');
});

it('does not let one failing start stop the others', function () {
    $h = newRecord();
    $h->actions->set('crm.ping', failTimes: 100);
    $h->identity->broken['broken'] = true;
    $h->publish('{ "specVersion": 1, "id": "wf_bad", "key": "bad", "kind": "automation", "title": "Bad", "entity": "application",
      "settings": { "notifyOwnersOnIncident": true, "owners": [ { "user": "broken" } ] },
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "x", "type": "action", "action": "crm.ping", "input": {} } ] }');
    $h->publish('{ "specVersion": 1, "id": "wf_good", "key": "good", "kind": "automation", "title": "Good", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": "changed" } ] }');
    changeStage($h, 'checked');
    $failed = array_values(array_filter($h->store->all('audit'), fn (AuditRecord $a) => $a->category === 'trigger' && $a->action === 'failed'));

    expect(instanceByKey($h, 'good')->status)->toBe(InstanceStatus::COMPLETED)
        ->and($failed)->toHaveCount(1)
        ->and($failed[0]->definitionId)->toBe('wf_bad')
        ->and(Json::decode($failed[0]->data)->error)->toBe("InvalidOperationException: The directory failed looking up 'broken'.");
});

// ── Definitions ───────────────────────────────────────────────────────────

it('brings back the old name with a rollback', function () {
    $h = newRecord();
    $v1 = '{ "specVersion": 1, "id": "wf_n", "key": "named", "kind": "automation", "title": "First name", "entity": "application",
      "trigger": { "type": "manual", "label": "Go" }, "steps": [] }';
    $h->publish($v1);
    $h->publish(str_replace('First name', 'Second name', $v1));
    $h->engine->rollback('wf_n', 1, behaviourAdmin());
    $summary = array_find($h->engine->listDefinitions(), fn (DefinitionSummary $d) => $d->id === 'wf_n');

    expect($summary->title)->toBe('First name')->and($summary->version)->toBe(3);
});

it('changes nothing when a rollback fails lint', function () {
    $h = newRecord();
    $h->publish('{ "specVersion": 1, "id": "wf_c", "key": "child", "kind": "automation", "title": "Child", "entity": "application",
      "trigger": { "type": "manual", "label": "Go" }, "steps": [] }');
    $v1 = '{ "specVersion": 1, "id": "wf_r", "key": "parent", "kind": "automation", "title": "Parent", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "s", "type": "subWorkflow", "workflow": "child", "wait": false } ] }';
    $h->publish($v1);
    $h->publish(str_replace('[ { "id": "s", "type": "subWorkflow", "workflow": "child", "wait": false } ]', '[]', $v1));
    $h->engine->archive('wf_c', behaviourAdmin());
    $draftBefore = Json::encode($h->engine->getDefinition('wf_r')->draft);

    expect(errorCode(fn () => $h->engine->rollback('wf_r', 1, behaviourAdmin())))->toBe('lintErrors')
        ->and(Json::encode($h->engine->getDefinition('wf_r')->draft))->toBe($draftBefore)
        ->and($h->engine->getDefinition('wf_r')->summary->version)->toBe(2)
        ->and(array_filter($h->store->all('audit'), fn (AuditRecord $a) => $a->action === 'rolledBack'))->toBeEmpty();
});

it('refuses to migrate a run whose waiting step moved', function () {
    $h = newRecord();
    $step = fn (string $first, string $second) => '{ "specVersion": 1, "id": "wf_m", "key": "mig", "kind": "automation", "title": "Mig", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" }, "steps": [ '.$first.', '.$second.' ] }';
    $wait = '{ "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } }';
    $notify = '{ "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": "x" }';
    $h->publish($step($wait, $notify));
    changeStage($h, 'checked');
    $h->publish($step($notify, $wait));

    expect(errorCode(fn () => $h->engine->migrate(instanceByKey($h, 'mig')->id, 2, null, behaviourAdmin())))->toBe('cannotMigrate');
    $h->publish($step($wait, $notify));
    expect($h->engine->migrate(instanceByKey($h, 'mig')->id, 3, null, behaviourAdmin())->version)->toBe(3);
});

it('refuses permission changes to unknown nodes and unpublished workflows', function () {
    $h = newRecord();
    $h->publish(ALL_APPROVAL);
    $h->engine->createDefinition('{ "specVersion": 1, "id": "wf_d", "key": "draftOnly", "kind": "automation", "title": "Draft", "trigger": { "type": "manual" }, "steps": [] }',
        behaviourAdmin());

    expect(errorCode(fn () => $h->engine->updatePermissions('wf_a', [['nope', []]], null, behaviourAdmin())))->toBe('unknownNode')
        ->and(errorCode(fn () => $h->engine->updatePermissions('wf_d', [['trigger', []]], null, behaviourAdmin())))->toBe('notPublished')
        ->and($h->engine->updatePermissions('wf_a', [['t', [(object) ['user' => 'rev3']]]], null, behaviourAdmin())->summary->version)->toBe(2);
});

// ── Tasks ─────────────────────────────────────────────────────────────────

it('refuses delegating a task to someone who already decided', function () {
    [$h, $task] = openApproval();
    $h->engine->decide($task, WorkflowActor::user('rev2', 'Rafiq'), 'approved', null, null);

    expect(errorCode(fn () => $h->engine->delegate($task, WorkflowActor::user('rev1', 'Rana'), 'rev2', null)))->toBe('alreadyAssigned')
        ->and($h->engine->delegate($task, WorkflowActor::user('rev1', 'Rana'), 'rev3', null)->assignees)->toBe(['rev3', 'rev2']);
});

it('settles a task reassigned to people who all decided', function () {
    [$h, $task] = openApproval();
    $h->engine->decide($task, WorkflowActor::user('rev1', 'Rana'), 'approved', null, null);
    $view = $h->engine->reassign($task, behaviourAdmin(), ['rev1'], 'rev2 left');
    $h->settle();

    expect($view->status)->toBe(TaskStatus::COMPLETED)
        ->and($view->outcome)->toBe('approved')
        ->and(instanceByKey($h, 'appr')->status)->toBe(InstanceStatus::COMPLETED);
});

it('refuses reassigning a task to someone the directory does not know', function () {
    [$h, $task] = openApproval();

    expect(errorCode(fn () => $h->engine->reassign($task, behaviourAdmin(), ['ghost'], null)))->toBe('unknownUser');
});

it('limits the size of decision answers', function () {
    [$h, $task] = openApproval();

    expect(errorCode(fn () => $h->engine->decide($task, WorkflowActor::user('rev1', 'Rana'), 'approved', null,
        (object) ['blob' => str_repeat('x', WorkflowEngine::MAX_DECISION_DATA_BYTES + 1)])))->toBe('tooLarge');
});

it('shows dates in templates the way people write them', function () {
    $h = engineHost(['title' => 'Q', 'stage' => 'new', 'deadline' => '2026-10-20', 'decidedAt' => '2026-10-19T14:30:00.000Z']);
    $h->publish('{ "specVersion": 1, "id": "wf_d", "key": "dates", "kind": "automation", "title": "Dates", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "subject": "Dates",
                   "body": ["Due ", { "ref": "entity.deadline" }, "; decided ", { "ref": "entity.decidedAt" }, "."] } ] }');
    changeStage($h, 'checked');

    expect(array_find($h->notifications->sent, fn ($n) => $n->subject === 'Dates')->body)->toBe('Due 20 Oct 2026; decided 19 Oct 2026, 14:30 UTC.');
});

it('shows a reminder\'s due time readably in the settings time zone', function (?string $zone, string $expected) {
    $h = newRecord();
    if ($zone !== null) {
        saveSettings($h, "{ \"version\": 1, \"timeZone\": \"{$zone}\" }");
    }
    $h->publish('{ "specVersion": 1, "id": "wf_r", "key": "rem", "kind": "automation", "title": "Rem", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "t", "type": "approval", "title": "Check it", "assignees": [ { "user": "rev1" } ], "rule": { "type": "any" }, "due": "P3D",
                   "reminders": ["P1D"], "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }');
    changeStage($h, 'checked');
    $h->advance((int) (2.5 * 86400000));
    $reminder = array_find($h->notifications->sent, fn ($n) => str_starts_with($n->subject ?? '', 'Reminder: Check it'));

    expect($reminder->body)->toBe("“Check it” is still waiting for you. It is due {$expected}.")
        ->and(Instant::format($h->store->all('tasks')[0]->dueAt))->toBe('2026-10-22T09:00:00.000Z');
})->with([
    'UTC' => [null, '22 Oct 2026, 09:00 UTC'],
    'Riyadh' => ['Asia/Riyadh', '22 Oct 2026, 12:00 Asia/Riyadh'],
]);

// ── People only ───────────────────────────────────────────────────────────

it('lets only a person apply a transition, and never a hidden or who-less one', function () {
    $h = engineHost(['title' => 'Q', 'status' => 'draft']);
    $h->publish(PEOPLE_LIFECYCLE);
    $rev = WorkflowActor::user('rev1', 'Rana', ['reviewer']);

    expect(errorCode(fn () => $h->engine->applyTransition('application', 'a1', 'expire', null, new WorkflowActor('anonymous'))))->toBe('notAllowed')
        ->and(errorCode(fn () => $h->engine->applyTransition('application', 'a1', 'expire', null, WorkflowActor::system())))->toBe('notAllowed')
        ->and(errorCode(fn () => $h->engine->applyTransition('application', 'a1', 'expire', null, $rev)))->toBe('notAllowed');
    $h->engine->applyTransition('application', 'a1', 'submit', null, $rev);
    expect(errorCode(fn () => $h->engine->applyTransition('application', 'a1', 'approve', null, $rev)))->toBe('notAllowed');
    // App code moves records through the system API, which skips who (and allows hidden transitions).
    $h->engine->applyTransitionAsSystem('application', 'a1', 'approve');
    expect($h->store->peekRecord('application', 'a1')->get('status'))->toBe('approved');
});

it('needs a person to start a manual workflow or decide a task', function () {
    [$h, $task] = openApproval();
    $anonymous = new WorkflowActor('anonymous');

    expect(errorCode(fn () => $h->engine->decide($task, $anonymous, 'approved', null, null)))->toBe('notAllowed')
        ->and(errorCode(fn () => $h->engine->startManual('appr', 'application', 'a1', null, $anonymous)))->toBe('notAllowed');
});

it('creates nothing when a record is read', function () {
    $h = engineHost(['title' => 'Q', 'status' => 'draft']);
    $h->publish(PEOPLE_LIFECYCLE);
    $audit = count($h->store->all('audit'));
    unset($h->store->tables['instances']);
    $view = $h->engine->getRecord('application', 'a1', WorkflowActor::user('rev1', 'Rana', ['reviewer']));

    expect($view->state)->toBe('draft')
        ->and(array_map(fn ($t) => $t->key, $view->transitions))->toContain('submit')
        ->and($h->store->all('instances'))->toBeEmpty()
        ->and(count($h->store->all('audit')))->toBe($audit);
});

// ── Webhooks and masking ──────────────────────────────────────────────────

it('encodes the values it inserts into webhook paths', function () {
    $h = engineHost(['title' => 'a/../admin?x=1#', 'stage' => 'new']);
    saveSettings($h, '{ "version": 1, "webhooks": { "allowedOrigins": ["https://api.example.com"] } }');
    $h->publish('{ "specVersion": 1, "id": "wf_w", "key": "hook", "kind": "automation", "title": "Hook", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "w", "type": "webhook", "url": "https://api.example.com", "method": "POST", "path": ["/items/", { "ref": "entity.title" }] } ] }');
    changeStage($h, 'checked');

    expect(array_map(fn ($c) => $c->url, $h->webhooks->calls))->toBe(['https://api.example.com/items/a%2F..%2Fadmin%3Fx%3D1%23']);
});

it('masks form answers marked sensitive in the audit and in views', function () {
    $h = newRecord();
    saveSettings($h, '{ "version": 1, "audit": { "maskedFields": ["input.ssn"] } }');
    // The answers are kept only for a form that asks for them.
    $h->publish(str_replace('"allowDelegation": true,', '"allowDelegation": true, "form": { "schemaVersion": 1, "id": "f", "title": "F", "pages": [ { "id": "p",
      "elements": [ { "kind": "field", "id": "f_ssn", "key": "ssn", "type": "text", "label": "SSN" } ] } ] },', ALL_APPROVAL));
    changeStage($h, 'checked');
    $task = $h->store->all('tasks')[0]->id;
    $h->engine->decide($task, WorkflowActor::user('rev1', 'Rana'), 'approved', null, (object) ['ssn' => '123-45-6789']);
    $h->engine->decide($task, WorkflowActor::user('rev2', 'Rafiq'), 'approved', null, (object) ['ssn' => '987-65-4321']);
    $h->settle();
    $audit = implode("\n", array_map(fn (AuditRecord $a) => $a->data ?? '', $h->store->all('audit')));
    $view = serialize($h->engine->getInstance(instanceByKey($h, 'appr')->id));

    expect($audit)->not->toContain('6789')->not->toContain('4321')
        ->and($view)->not->toContain('6789')->not->toContain('4321')
        ->and($h->engine->getInstance(instanceByKey($h, 'appr')->id)->outputs->t->data->ssn)->toBe('***');
});

it('masks sensitive values copied into variables and triggers', function () {
    $h = engineHost(['title' => 'Q', 'stage' => 'new', 'applicantEmail' => 'ali@private.example', 'amount' => 5000]);
    saveSettings($h, '{ "version": 1, "audit": { "maskedFields": ["application.amount"] } }');
    $h->publish('{ "specVersion": 1, "id": "wf_v", "key": "copy", "kind": "automation", "title": "Copy", "entity": "application",
      "variables": [ { "id": "v", "key": "v", "type": "text" } ],
      "trigger": { "type": "fieldChanged", "field": "amount" },
      "steps": [ { "id": "s", "type": "setVariables", "values": [ { "variable": "v", "value": { "ref": "entity.applicantEmail" } } ] },
                 { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }');
    $h->store->changeRecord('application', 'a1', (object) ['amount' => 7654321]);
    $h->settle();
    $audit = implode("\n", array_map(fn (AuditRecord $a) => $a->data ?? '', $h->store->all('audit')));
    $view = serialize($h->engine->getInstance(instanceByKey($h, 'copy')->id));

    expect($audit)->not->toContain('ali@private.example')->not->toContain('7654321')
        ->and($view)->not->toContain('ali@private.example')->not->toContain('7654321');
});

// ── Test runs ─────────────────────────────────────────────────────────────

it('masks sensitive values in test runs and changes nothing real', function () {
    $h = engineHost(['title' => 'Q', 'stage' => 'new', 'applicantEmail' => 'ali@private.example']);
    $result = $h->engine->simulate('{ "specVersion": 1, "id": "wf_s", "key": "sim", "kind": "automation", "title": "Sim", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["Mail ", { "ref": "entity.applicantEmail" }] },
                 { "id": "f", "type": "setFields", "values": [ { "field": "stage", "value": { "const": "done" } } ] } ] }',
        new SimulationRequest('a1', trigger: (object) ['from' => 'new', 'to' => 'checked']), behaviourAdmin());

    expect($result->error)->toBeNull()
        ->and(serialize($result))->not->toContain('ali@private.example')
        ->and($result->record->applicantEmail)->toBe('***')
        ->and($result->record->stage)->toBe('done')
        ->and(array_map(fn ($e) => "{$e->kind}.{$e->action}", $result->log))->toContain('notify.email')
        ->and($h->store->peekRecord('application', 'a1')->get('stage'))->toBe('new')
        ->and($h->notifications->sent)->toBeEmpty()
        ->and($h->store->all('instances'))->toBeEmpty();
});

it('warns when a test run\'s record would not get the lifecycle', function () {
    $h = engineHost(['title' => 'Q', 'track' => 'visa']);
    $definition = '{ "specVersion": 1, "id": "lc_s", "key": "passportSim", "kind": "lifecycle", "title": "Passport", "entity": "application",
      "lifecycle": { "field": "status",
        "appliesWhen": { "match": "all", "conditions": [ { "ref": "entity.track", "op": "eq", "value": { "const": "passport" } } ] },
        "states": [ { "id": "a", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "b", "key": "approved", "label": "Done", "category": "terminal" } ],
        "transitions": [ { "id": "t", "key": "finish", "label": "Finish", "from": ["a"], "to": "b", "who": [ { "role": "admin" } ] } ] } }';
    $visa = $h->engine->simulate($definition, new SimulationRequest('a1', transition: 'finish'), behaviourAdmin());
    $passport = $h->engine->simulate($definition, new SimulationRequest('a1', values: (object) ['track' => 'passport']), behaviourAdmin());

    expect($visa->error)->toBeNull()
        ->and(array_map(fn ($p) => "{$p->code}|{$p->severity}", $visa->problems))->toContain('lifecycleNotSelected|warning')
        ->and($visa->record->status)->toBe('approved')
        ->and(array_map(fn ($p) => $p->code, $passport->problems))->not->toContain('lifecycleNotSelected');
});

it('replays at most fifty events in a test run', function () {
    $h = newRecord();
    $events = array_map(fn () => new SimulationEvent('advance', duration: 'PT1H'), range(1, 51));

    expect(errorCode(fn () => $h->engine->simulate(ALL_APPROVAL, new SimulationRequest('a1', events: $events), behaviourAdmin())))->toBe('tooManyEvents');
});

// ── Audit and errors ──────────────────────────────────────────────────────

it('keeps the chain verifiable after purging the newest entries', function () {
    $h = newRecord();
    saveSettings($h, '{ "version": 1, "audit": { "retentionDays": { "settings": 1, "definition": 1 } } }');
    $h->clock->advance(3 * 86400000);

    expect($h->engine->purgeAudit(behaviourAdmin()))->toBeGreaterThan(0);
    saveSettings($h, '{ "version": 1, "timeZone": "UTC" }');
    $seqs = array_map(fn (AuditRecord $a) => $a->seq, $h->store->all('audit'));
    expect($h->engine->verifyAudit()->valid)->toBeTrue()
        ->and(count(array_unique($seqs)))->toBe(count($seqs));
    // Removing an entry no purge covers breaks the chain.
    unset($h->store->tables['audit'][array_key_last($h->store->tables['audit'])]);
    expect($h->engine->verifyAudit()->valid)->toBeFalse();
});

it('describes an error by its innermost cause, and a workflow refusal by its own message', function () {
    $wrapped = new RuntimeException('Saving failed, see the inner exception.', 0, new InvalidOperationException('The disk is full.'));
    $refusal = new RuntimeException('outer', 0, new WorkflowException('noRecord', 'There is no record to update.'));

    expect(WorkflowEngine::describeError($wrapped))->toBe('InvalidOperationException: The disk is full.')
        ->and(WorkflowEngine::describeError($refusal))->toBe('There is no record to update.')
        ->and(WorkflowEngine::describeError(new WorkflowException('x', 'Plain refusal.')))->toBe('Plain refusal.');
});
