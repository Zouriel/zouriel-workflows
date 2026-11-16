<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Engine\Views\ResetItem;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;
use Zouriel\Workflows\Core\WorkflowException;

if (! specAvailable()) {
    test('reset and separation-of-duties tests use the shared registry in spec/')->skip('spec/fixtures not found');

    return;
}

// The reset list's undo and maker-checker paths the shared fixtures cannot reach (undoable app actions, ordering of
// work done at one moment, stale lists, undo writes starting nothing, refusal texts, admin reassign, escalation),
// ported from .NET ResetUndoTests and SeparationOfDutiesTests.

function resetAdmin(): WorkflowActor
{
    return WorkflowActor::user('admin', 'Admin', ['admin']);
}

function refusal(callable $act): WorkflowException
{
    try {
        $act();
    } catch (WorkflowException $e) {
        return $e;
    }
    throw new LogicException('Expected a refusal.');
}

/** @return list<string> */
function selectedIds(WorkflowTestHost $h): array
{
    return array_values(array_map(fn (ResetItem $i) => $i->id, array_filter($h->engine->getResetPreview('application', 'a1', resetAdmin())->items, fn (ResetItem $i) => $i->selected)));
}

function submitted(bool $undoableInvoice): WorkflowTestHost
{
    $h = engineHost();
    $h->actions->set('finance.createInvoice', (object) ['invoiceNo' => 'INV-7']);
    if ($undoableInvoice) {
        $h->actions->makeUndoable('finance.createInvoice');
    }
    $h->publish('{ "specVersion": 1, "id": "lc_r", "key": "review", "kind": "lifecycle", "title": "Review", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
          "steps": [
            { "id": "doc", "type": "createRecord", "entity": "document", "values": [ { "field": "verified", "value": { "const": true } } ] },
            { "id": "inv", "type": "action", "action": "finance.createInvoice", "input": { "amount": { "const": 100 } } },
            { "id": "mail", "type": "notify", "channel": "email", "to": [ { "field": "entity.applicant" } ], "body": "Thanks" } ] } ],
        "reset": { "who": [ { "role": "admin" } ] } } }');
    $h->store->createRecord('application', 'a1', (object) ['title' => 'Q', 'applicant' => 'ali']);
    $h->settle();
    $h->engine->applyTransition('application', 'a1', 'submit', null, WorkflowActor::user('ali', 'Ali'));
    $h->settle();

    return $h;
}

function documents(WorkflowTestHost $h): int
{
    return count(array_filter($h->store->all('records'), fn ($r) => $r->entity === 'document'));
}

const SETTLE_LIFECYCLE = <<<'JSON'
{ "specVersion": 1, "id": "lc_s", "key": "settle", "kind": "lifecycle", "title": "Settle", "entity": "application",
  "lifecycle": { "field": "status", "states": [
    { "id": "a", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
    "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
      "steps": [ { "id": "hi", "type": "setFields", "values": [ { "field": "priority", "value": { "const": "high" } } ] } ] } ],
    "reset": { "who": [ { "role": "admin" } ], "cancelAutomations": false,
      "steps": [ { "id": "rn", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "Started over" } } ] } ] } } }
JSON;

function submittedPriority(string ...$also): WorkflowTestHost
{
    $h = engineHost();
    $h->publish(SETTLE_LIFECYCLE);
    foreach ($also as $definition) {
        $h->publish($definition);
    }
    $h->store->createRecord('application', 'a1', (object) ['title' => 'Q', 'applicant' => 'ali', 'priority' => 'normal']);
    $h->settle();
    $h->engine->applyTransition('application', 'a1', 'submit', null, WorkflowActor::user('ali', 'Ali'));
    $h->settle();

    return $h;
}

it('deletes what the workflow created and calls the action\'s undo with its output', function () {
    $h = submitted(undoableInvoice: true);
    $preview = $h->engine->getResetPreview('application', 'a1', resetAdmin());

    expect(documents($h))->toBe(1)
        ->and(array_map(fn (ResetItem $i) => [$i->kind, $i->selected, $i->undoable], $preview->items))
        ->toBe([['created', true, true], ['action', true, true], ['message', false, false]]);
    $h->engine->reset('application', 'a1', null, resetAdmin(), undo: selectedIds($h));
    $h->settle();
    expect(documents($h))->toBe(0)
        ->and($h->actions->undone)->toHaveCount(1)
        ->and($h->actions->undone[0]->output->invoiceNo)->toBe('INV-7')
        ->and($h->actions->undone[0]->actor->id)->toBe('admin')
        // The undo's idempotency key is the step that ran the action, so a retried reset can tell it already undid it.
        ->and("action:{$h->actions->undone[0]->idempotencyKey}")->toBe($preview->items[1]->id);
});

it('leaves unticked items and refuses things that cannot be undone', function () {
    $h = submitted(undoableInvoice: false);
    $items = $h->engine->getResetPreview('application', 'a1', resetAdmin())->items;
    $action = array_find($items, fn (ResetItem $i) => $i->kind === 'action');
    $message = array_find($items, fn (ResetItem $i) => $i->kind === 'message');

    expect($action->undoable)->toBeFalse()->and($action->selected)->toBeFalse()
        ->and(refusal(fn () => $h->engine->reset('application', 'a1', null, resetAdmin(), undo: [$message->id]))->errorCode)->toBe('notUndoable');
    // Nothing ticked: the reset still happens and the created document stays.
    $h->engine->reset('application', 'a1', null, resetAdmin());
    expect(documents($h))->toBe(1)->and($h->store->peekRecord('application', 'a1')->get('status'))->toBe('draft');
});

it('keeps the order of work done at one moment: first value before, newest action undone first', function () {
    // The clock never moves, so every step has the same start time; only the journal's seq orders them.
    $h = engineHost();
    $h->actions->makeUndoable('finance.createInvoice');
    $h->actions->makeUndoable('crm.ping');
    $h->publish('{ "specVersion": 1, "id": "lc_o", "key": "ordered", "kind": "lifecycle", "title": "Ordered", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
          "steps": [ { "id": "hi", "type": "setFields", "values": [ { "field": "priority", "value": { "const": "high" } } ] } ] } ],
        "reset": { "who": [ { "role": "admin" } ] } } }');
    $h->publish('{ "specVersion": 1, "id": "wf_1", "key": "first", "kind": "automation", "title": "First", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["high"] },
      "steps": [ { "id": "ping", "type": "action", "action": "crm.ping", "input": {} },
        { "id": "na", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "a" } }, { "field": "priority", "value": { "const": "low" } } ] } ] }');
    $h->publish('{ "specVersion": 1, "id": "wf_2", "key": "second", "kind": "automation", "title": "Second", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["low"] },
      "steps": [ { "id": "inv", "type": "action", "action": "finance.createInvoice", "input": { "amount": { "const": 100 } } },
        { "id": "nb", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "b" } } ] } ] }');
    $h->store->createRecord('application', 'a1', (object) ['title' => 'Q', 'applicant' => 'ali', 'priority' => 'normal']);
    $h->settle();
    $h->engine->applyTransition('application', 'a1', 'submit', null, WorkflowActor::user('ali', 'Ali'));
    $h->settle();
    $seqs = array_map(fn (StepRecord $s) => (int) Json::decode($s->undo)->seq, array_filter($h->store->all('steps'), fn (StepRecord $s) => $s->undo !== null));
    $items = $h->engine->getResetPreview('application', 'a1', resetAdmin())->items;
    $title = fn (string $id) => array_find($items, fn (ResetItem $i) => $i->id === $id)->title;

    expect($h->store->peekRecord('application', 'a1')->get('notes'))->toBe('b')
        ->and(count(array_unique($seqs)))->toBe(count($seqs))
        ->and($title('field:application:a1:notes'))->toEndWith('back to nothing')
        ->and($title('field:application:a1:priority'))->toEndWith('back to Normal')
        ->and(array_values(array_map(fn (ResetItem $i) => $i->title, array_filter($items, fn (ResetItem $i) => $i->kind === 'action'))))
        ->toBe(['Undo “Ping CRM”', 'Undo “Create invoice”']);
    $h->engine->reset('application', 'a1', null, resetAdmin(), undo: selectedIds($h));
    $h->settle();
    expect(array_map(fn ($u) => $u->actionKey, $h->actions->undone))->toBe(['finance.createInvoice', 'crm.ping'])
        ->and($h->store->peekRecord('application', 'a1')->get('notes'))->toBeNull();
});

it('fails a reset whose list changed since it was opened, even when its items are still there', function () {
    $h = submittedPriority();
    $opened = $h->engine->getResetPreview('application', 'a1', resetAdmin());

    expect($h->engine->getResetPreview('application', 'a1', resetAdmin())->token)->toBe($opened->token);
    // Someone edits the field after the list was opened: the item is still there, but its value is newer.
    $h->store->changeRecord('application', 'a1', (object) ['priority' => 'low']);
    $h->settle();
    expect(refusal(fn () => $h->engine->reset('application', 'a1', null, resetAdmin(), undo: ['field:application:a1:priority'], token: $opened->token))->errorCode)
        ->toBe('resetChanged')
        ->and($h->store->peekRecord('application', 'a1')->get('priority'))->toBe('low')
        ->and($h->store->peekRecord('application', 'a1')->get('status'))->toBe('submitted');
    $fresh = $h->engine->getResetPreview('application', 'a1', resetAdmin());
    expect($fresh->token)->not->toBe($opened->token);
    $h->engine->reset('application', 'a1', null, resetAdmin(), undo: ['field:application:a1:priority'], token: $fresh->token);
    expect($h->store->peekRecord('application', 'a1')->get('priority'))->toBe('normal');
});

it('starts no automation when it puts a field back, though the same write by a person does', function () {
    $h = submittedPriority('{ "specVersion": 1, "id": "wf_n", "key": "onNormal", "kind": "automation", "title": "On normal", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["normal"] },
      "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "score", "value": { "const": 99 } } ] } ] }');
    $h->engine->reset('application', 'a1', null, resetAdmin(), undo: ['field:application:a1:priority']);
    $h->settle();

    expect($h->store->peekRecord('application', 'a1')->get('priority'))->toBe('normal')
        ->and($h->store->peekRecord('application', 'a1')->get('score'))->toBeNull()
        ->and(array_filter($h->store->all('instances'), fn (InstanceRecord $i) => $i->definitionKey === 'onNormal'))->toBeEmpty()
        ->and(array_filter($h->store->all('outbox'), fn (OutboxRecord $o) => $o->processedAt === null))->toBeEmpty();
    $h->store->changeRecord('application', 'a1', (object) ['priority' => 'high']);
    $h->store->changeRecord('application', 'a1', (object) ['priority' => 'normal']);
    $h->settle();
    expect($h->store->peekRecord('application', 'a1')->get('score'))->toBe(99.0);
});

// ── Separation of duties (spec §7 makerChecker) ───────────────────────────

/** @return array{0: WorkflowTestHost, 1: string} */
function separatedApproval(string $makerChecker, string $extra = ''): array
{
    $h = engineHost(['title' => 'Q', 'stage' => 'new']);
    $h->publish('{ "specVersion": 1, "id": "wf_sod", "key": "sod", "kind": "automation", "title": "Sod", "entity": "application",
      "trigger": { "type": "manual", "label": "Go" },
      "steps": [ { "id": "ap", "type": "approval", "assignees": [ { "role": "reviewer" }, { "user": "fin1" } ], "rule": { "type": "any" }, "due": "P3D",
                   "allowDelegation": true, '.$extra.' "makerChecker": '.$makerChecker.', "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }');
    $h->engine->startManual('sod', 'application', 'a1', null, WorkflowActor::user('rev1', 'Rana'));
    $h->settle();

    return [$h, $h->store->all('tasks')[0]->id];
}

it('refuses the initiator with the plain message', function () {
    [$h, $task] = separatedApproval('{ "excludeInitiator": true }');
    $e = refusal(fn () => $h->engine->decide($task, WorkflowActor::user('rev1', 'Rana'), 'approved', null, null));

    expect($e->errorCode)->toBe('segregationOfDuties')
        ->and($e->getMessage())->toBe('You cannot approve this: the person who started it cannot approve it.');
});

it('tells which role a person handed the task lacks', function () {
    [$h, $task] = separatedApproval('{ "requiredRole": "finance" }');
    $e = refusal(fn () => $h->engine->delegate($task, WorkflowActor::user('fin1', 'Farah'), 'ali', null));

    expect($e->errorCode)->toBe('segregationOfDuties')->and($e->getMessage())->toBe('Ali Hassan cannot approve this: it needs the Finance role.');
});

it('does not let an admin assign a refused person, and changes nothing', function () {
    [$h, $task] = separatedApproval('{ "excludeInitiator": true }');
    $before = $h->store->all('tasks')[0]->assignees;

    expect(refusal(fn () => $h->engine->reassign($task, resetAdmin(), ['rev2', 'rev1'], 'swap'))->errorCode)->toBe('segregationOfDuties')
        ->and($h->store->all('tasks')[0]->assignees)->toBe($before)
        ->and($h->engine->reassign($task, resetAdmin(), ['rev2'], 'swap')->assignees)->toBe(['rev2']);
});

it('skips a refused person silently when escalating', function () {
    [$h, $task] = separatedApproval('{ "excludeInitiator": true }', '"escalation": { "after": "PT1H", "assignees": [ { "user": "rev1" }, { "user": "ali" } ] },');
    $h->advance(2 * 3600000);
    $t = $h->store->all('tasks')[0];

    expect($t->escalationLevel)->toBe(1)
        ->and(Json::decode($t->assignees))->not->toContain('rev1')->toContain('ali')
        ->and(refusal(fn () => $h->engine->decide($task, WorkflowActor::user('rev1', 'Rana'), 'approved', null, null))->errorCode)->toBe('segregationOfDuties');
});

it('treats an empty makerChecker as no rule', function () {
    [$h, $task] = separatedApproval('{}');
    $h->engine->decide($task, WorkflowActor::user('rev1', 'Rana'), 'approved', null, null);
    $created = array_find($h->store->all('audit'), fn (AuditRecord $a) => $a->category === 'task' && $a->action === 'created');

    expect($created->data)->not->toContain('excluded');
});
