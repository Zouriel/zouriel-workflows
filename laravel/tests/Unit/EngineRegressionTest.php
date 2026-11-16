<?php

use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Engine\EngineOptions;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\GlobalSettings;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\InMemoryStore;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;
use Zouriel\Workflows\Core\Time\ManualClock;

if (! specAvailable()) {
    test('regression tests use the shared registry in spec/')->skip('spec/fixtures not found');

    return;
}

// Regressions found in review of the .NET engine (EngineRegressionTests, TenancyTests): each failed there before its fix.

/** @return list<InstanceRecord> */
function instancesOf(WorkflowTestHost $h, string $key): array
{
    return array_values(array_filter($h->store->all('instances'), fn (InstanceRecord $i) => $i->definitionKey === $key));
}

it('keeps draining queued instances when a dequeued one finishes at once', function () {
    $h = engineHost(['title' => 'Q', 'stage' => 'new']);
    $h->publish('{ "specVersion": 1, "id": "wf_q", "key": "serial", "kind": "automation", "title": "Serial", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "b", "type": "branch", "paths": [ { "id": "p", "label": "checked", "when": { "ref": "trigger.to", "op": "eq", "value": { "const": "checked" } },
          "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] } ] } ] }');
    foreach (['checked', 'done', 'new'] as $stage) {
        $h->store->changeRecord('application', 'a1', (object) ['stage' => $stage]);
        $h->settle();
        $h->clock->advance(1000);
    }
    expect(array_map(fn (InstanceRecord $i) => $i->status, instancesOf($h, 'serial')))->toContain(InstanceStatus::QUEUED);
    $h->advance(2 * 3600000);

    expect(array_map(fn (InstanceRecord $i) => $i->status, instancesOf($h, 'serial')))->toBe(array_fill(0, 3, InstanceStatus::COMPLETED));
});

it('ends an onEnter run that moves its own record exactly once', function () {
    $h = engineHost(['title' => 'Q', 'status' => 'draft']);
    $h->publish('{ "specVersion": 1, "id": "lc_t", "key": "lct", "kind": "lifecycle", "title": "LC", "entity": "application",
      "lifecycle": { "field": "status", "mode": "enforce",
        "states": [
          { "id": "st_draft", "key": "draft", "label": "Draft", "category": "initial" },
          { "id": "st_sub", "key": "submitted", "label": "Submitted", "category": "active",
            "onEnter": [
              { "id": "at1", "type": "applyTransition", "lifecycle": "lct", "transition": "approve", "ifNotAllowed": "fail" },
              { "id": "n1", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["after leaving"] } ] },
          { "id": "st_ok", "key": "approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [
          { "id": "tr_submit", "key": "submit", "label": "Submit", "from": ["st_draft"], "to": "st_sub", "who": [ { "role": "reviewer" } ] },
          { "id": "tr_ok", "key": "approve", "label": "Approve", "from": ["st_sub"], "to": "st_ok", "hidden": true } ] } }');
    $h->engine->applyTransition('application', 'a1', 'submit', null, WorkflowActor::user('rev1', 'Rana', ['reviewer']));
    $h->settle();
    $run = array_values(array_filter($h->store->all('instances'), fn (InstanceRecord $i) => $i->reason === 'onEnter:st_sub'))[0];
    $endings = array_filter($h->store->all('audit'), fn (AuditRecord $a) => $a->instanceId === $run->id && $a->category === 'instance'
        && in_array($a->action, ['cancelled', 'completed', 'failed'], true));

    // The run moving the record is left to finish its own steps (it is not cancelled by the record leaving its state).
    expect($endings)->toHaveCount(1)
        ->and(array_values($endings)[0]->action)->toBe('completed')
        ->and(array_map(fn ($n) => $n->body, $h->notifications->sent))->toContain('after leaving')
        ->and($h->store->peekRecord('application', 'a1')->get('status'))->toBe('approved');
});

it('keeps an instance in incident when one of two incidents is retried', function () {
    $h = engineHost(['title' => 'Q', 'stage' => 'new']);
    $h->actions->set('crm.ping', failTimes: 2);
    $h->publish('{ "specVersion": 1, "id": "wf_p", "key": "par", "kind": "automation", "title": "Par", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "p", "type": "parallel", "join": { "type": "all" }, "paths": [
         { "id": "a", "label": "A", "steps": [ { "id": "x1", "type": "action", "action": "crm.ping", "input": {} } ] },
         { "id": "b", "label": "B", "steps": [ { "id": "x2", "type": "action", "action": "crm.ping", "input": {} } ] } ] } ] }');
    $h->store->changeRecord('application', 'a1', (object) ['stage' => 'checked']);
    $h->settle();
    $id = instancesOf($h, 'par')[0]->id;

    expect($h->engine->retry($id, WorkflowActor::user('admin', 'Admin'))->status)->toBe(InstanceStatus::INCIDENT)
        ->and($h->engine->retry($id, WorkflowActor::user('admin', 'Admin'))->status)->toBe(InstanceStatus::COMPLETED);
});

it('does not let a stateEntered automation start itself again', function () {
    $h = engineHost(['title' => 'Q', 'status' => 'draft']);
    $h->publish('{ "specVersion": 1, "id": "lc_l", "key": "loop", "kind": "lifecycle", "title": "Loop", "entity": "application",
      "lifecycle": { "field": "status", "mode": "enforce",
        "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                    { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
        "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                         { "id": "t2", "key": "back", "label": "Back", "from": ["s"], "to": "d", "hidden": true },
                         { "id": "t3", "key": "resubmit", "label": "Resubmit", "from": ["d"], "to": "s", "hidden": true } ] } }');
    $h->publish('{ "specVersion": 1, "id": "wf_e", "key": "onSub", "kind": "automation", "title": "On submitted", "entity": "application",
      "trigger": { "type": "stateEntered", "lifecycle": "loop", "states": ["submitted"] },
      "steps": [ { "id": "a1", "type": "applyTransition", "lifecycle": "loop", "transition": "back", "ifNotAllowed": "skip" },
                 { "id": "a2", "type": "applyTransition", "lifecycle": "loop", "transition": "resubmit", "ifNotAllowed": "skip" } ] }');
    $h->engine->applyTransition('application', 'a1', 'submit', null, WorkflowActor::user('rev1', 'Rana', ['reviewer']));
    $h->settle();

    expect(instancesOf($h, 'onSub'))->toHaveCount(1)->and($h->store->peekRecord('application', 'a1')->get('status'))->toBe('submitted');
});

it('never queues starts of a signal about no record', function () {
    $h = engineHost();
    $h->publish('{ "specVersion": 1, "id": "wf_n", "key": "nightly", "kind": "automation", "title": "Nightly",
      "trigger": { "type": "signal", "signal": "nightly" },
      "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }');
    $h->engine->signal('nightly', null, null, null);
    $h->engine->signal('nightly', null, null, null);

    expect(array_map(fn (InstanceRecord $i) => $i->status, instancesOf($h, 'nightly')))->toBe([InstanceStatus::WAITING, InstanceStatus::WAITING]);
});

it('fires a date trigger for records that existed before it was published', function () {
    $h = engineHost(['title' => 'Q', 'deadline' => '2026-10-25']);
    $h->publish('{ "specVersion": 1, "id": "wf_d", "key": "due", "kind": "automation", "title": "Due", "entity": "application",
      "trigger": { "type": "dateReached", "field": "deadline", "offset": "-P1D" },
      "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["due"] } ] }');
    expect(instancesOf($h, 'due'))->toBeEmpty();
    $h->advance(10 * 86400000);

    expect(instancesOf($h, 'due'))->toHaveCount(1)
        ->and(json_decode(instancesOf($h, 'due')[0]->triggerJson)->at)->toBe('2026-10-24T09:00:00.000Z');
});

it('keeps change events not yet processed when a workflow is published again', function () {
    $h = engineHost(['title' => 'Q', 'stage' => 'new']);
    $json = '{ "specVersion": 1, "id": "wf_r", "key": "rep", "kind": "automation", "title": "Rep", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["x"] } ] }';
    $h->publish($json);
    $h->clock->advance(1000);
    $h->store->changeRecord('application', 'a1', (object) ['stage' => 'checked']); // committed, the outbox not processed yet
    $h->clock->advance(1000);
    $h->publish(str_replace('"Rep"', '"Rep v2"', $json));
    $h->settle();

    expect(instancesOf($h, 'rep'))->toHaveCount(1);
});

it('lets tenants sharing a store read, purge and verify only their own audit chain', function () {
    $registry = WorkflowRegistry::fromJson(file_get_contents(__DIR__.'/../../../spec/fixtures/engine/_registry.json'));
    $clock = new ManualClock(1_800_000_000_000);
    $store = new InMemoryStore($registry, $clock);
    $a = new WorkflowEngine(new EngineOptions($registry, $store, clock: $clock, tenant: 'a'));
    $b = new WorkflowEngine(new EngineOptions($registry, $store, clock: $clock, tenant: 'b'));
    $admin = WorkflowActor::user('admin', 'Admin', ['admin']);
    $definition = fn (string $id) => "{ \"specVersion\": 1, \"id\": \"{$id}\", \"key\": \"{$id}\", \"kind\": \"automation\", \"title\": \"T\", \"entity\": \"application\",
      \"trigger\": { \"type\": \"created\" }, \"steps\": [] }";
    $a->createDefinition($definition('d1'), $admin);
    $b->createDefinition($definition('d2'), $admin);
    $b->createDefinition($definition('d3'), $admin);
    $seqs = fn (array $entries) => array_map(fn (AuditRecord $e) => $e->seq, $entries);

    expect($seqs($a->queryAudit(new AuditQuery)))->toBe([1])
        ->and($seqs($b->queryAudit(new AuditQuery)))->toBe([2, 1])
        // The engine's own tenant wins over the query's.
        ->and(array_unique(array_map(fn (AuditRecord $e) => $e->tenant, $b->queryAudit(new AuditQuery('a')))))->toBe(['b'])
        ->and($store->begin()->queryAudit(new AuditQuery))->toBeEmpty()
        ->and($a->verifyAudit()->valid)->toBeTrue()
        ->and($b->verifyAudit()->valid)->toBeTrue();

    [, $revision] = $a->getSettings();
    $a->saveSettings(GlobalSettings::parse('{ "version": 1, "audit": { "retentionDays": { "definition": 1 } } }'), $revision, $admin);
    $clock->advance(3 * 86400000);
    expect($a->purgeAudit($admin))->toBe(1)
        ->and($a->queryAudit(new AuditQuery(category: 'definition')))->toBeEmpty()
        ->and($b->queryAudit(new AuditQuery(category: 'definition')))->toHaveCount(2)
        ->and($a->verifyAudit()->valid)->toBeTrue()
        ->and($b->verifyAudit()->valid)->toBeTrue();
});
