<?php

use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Engine\SlaClocks;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;
use Zouriel\Workflows\Core\Time\Instant;

if (! specAvailable()) {
    test('SLA pause tests use the shared registry in spec/')->skip('spec/fixtures not found');

    return;
}

// What a kept SLA clock does under the interventions the shared fixtures cannot reach (force state, cancel, observe
// mode, migrate, archive and restore, parked timers), ported from .NET SlaPauseEngineTests.

const DAY = 86400000;

function slaLifecycle(string $mode = 'enforce', string $sla = '"pauseOnStatus": ["w"], "pauseOnEvent": ["payment.received"], "resumeOnEvent": ["nightly"], '): string
{
    return '{ "specVersion": 1, "id": "lc_s", "key": "slaLc", "kind": "lifecycle", "title": "SLA", "entity": "application",
      "lifecycle": { "field": "status", "mode": "'.$mode.'",
        "states": [
          { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
          { "id": "r", "key": "underReview", "label": "Under review", "category": "active", "sla": { "within": "P5D", '.$sla.'"steps": [] } },
          { "id": "w", "key": "submitted", "label": "Awaiting information", "category": "active" },
          { "id": "h", "key": "withdrawn", "label": "On hold", "category": "active" },
          { "id": "x", "key": "approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [
          { "id": "t1", "key": "start", "label": "Start", "from": ["d"], "to": "r", "who": [ { "role": "reviewer" } ] },
          { "id": "t2", "key": "ask", "label": "Ask", "from": ["r"], "to": "w", "who": [ { "role": "reviewer" } ] },
          { "id": "t3", "key": "back", "label": "Back", "from": ["w", "h"], "to": "r", "who": [ { "role": "reviewer" } ] },
          { "id": "t4", "key": "hold", "label": "Hold", "from": ["w", "r"], "to": "h", "who": [ { "role": "reviewer" } ] },
          { "id": "t5", "key": "approve", "label": "Approve", "from": ["r"], "to": "x", "who": [ { "role": "reviewer" } ] } ] } }';
}

function reviewer(): WorkflowActor
{
    return WorkflowActor::user('rev1', 'Rana', ['reviewer']);
}

function slaAdmin(): WorkflowActor
{
    return WorkflowActor::user('admin', 'Admin', ['admin']);
}

function started(string $mode = 'enforce'): WorkflowTestHost
{
    $h = engineHost();
    $h->publish(slaLifecycle($mode));
    $h->store->createRecord('application', 'a1', (object) ['title' => 'A']);
    $h->settle();
    $h->engine->applyTransition('application', 'a1', 'start', null, reviewer());

    return $h;
}

/** Review started, two days in review, then waiting for information: 3 days left on a paused clock. */
function paused(string $mode = 'enforce'): WorkflowTestHost
{
    $h = started($mode);
    $h->advance(2 * DAY);
    $h->engine->applyTransition('application', 'a1', 'ask', null, reviewer());
    $h->settle();

    return $h;
}

function recordInstance(WorkflowTestHost $h): InstanceRecord
{
    return array_values(array_filter($h->store->all('instances'), fn (InstanceRecord $i) => $i->kind === InstanceKinds::RECORD))[0];
}

/** @return list<BookmarkRecord> */
function timers(WorkflowTestHost $h): array
{
    return $h->store->all('bookmarks');
}

function auditCount(WorkflowTestHost $h, string $action): int
{
    return count(array_filter($h->store->all('audit'), fn (AuditRecord $a) => $a->action === $action));
}

/** The record's only timer is due this long from now. */
function dueIn(WorkflowTestHost $h, int $ms): void
{
    expect(timers($h))->toHaveCount(1)
        ->and(Instant::format(timers($h)[0]->dueAt))->toBe(Instant::format($h->clock->now() + $ms));
}

/** Parks the record's SLA timer the way repeated failures do (locked for ever, the instance in incident). */
function parkSla(WorkflowTestHost $h): void
{
    $s = $h->store->begin();
    $b = $s->bookmark(timers($h)[0]->id);
    $b->lockedUntil = Instant::MAX;
    $b->attempts = 10;
    $b->error = 'boom';
    $r = $s->instance(recordInstance($h)->id);
    $r->status = InstanceStatus::INCIDENT;
    $r->error = 'A timer (sla:r) failed 10 times and was stopped: boom';
    $s->updateBookmark($b);
    $s->updateInstance($r);
    $s->commit();
}

it('keeps a paused clock in the record instance and plans nothing', function () {
    $h = paused();
    $clocks = SlaClocks::read(recordInstance($h));

    expect(timers($h))->toBeEmpty()
        ->and(array_keys($clocks))->toBe(['r'])
        ->and($clocks['r']->leftMs)->toBe(3 * DAY)
        ->and($clocks['r']->reasons)->toBe(['status']);
});

it('drops a paused clock on a forced state and starts the state fresh', function () {
    $h = paused();
    $h->engine->forceState('application', 'a1', 'underReview', 'fix', false, slaAdmin());
    $h->settle();

    expect(recordInstance($h)->data)->toBe('{}')->and(auditCount($h, 'slaResumed'))->toBe(0);
    dueIn($h, 5 * DAY);
});

it('drops a paused clock on a forced state that skips steps, too', function () {
    $h = paused();
    $h->engine->forceState('application', 'a1', 'withdrawn', 'fix', true, slaAdmin());
    expect(SlaClocks::read(recordInstance($h)))->toBeEmpty();
    $h->engine->forceState('application', 'a1', 'underReview', 'fix', true, slaAdmin());
    dueIn($h, 5 * DAY);
});

it('drops a paused clock when the record instance is cancelled', function () {
    $h = paused();
    $h->engine->cancel(recordInstance($h)->id, 'test', slaAdmin());

    expect(recordInstance($h)->data)->toBe('{}')->and(recordInstance($h)->status)->toBe(InstanceStatus::CANCELLED);
});

it('drops a paused clock when an outside write leaves the lifecycle out of sync', function () {
    $h = paused('observe');
    // From "Awaiting information" no transition leads to "approved".
    $h->store->changeRecord('application', 'a1', (object) ['status' => 'approved']);
    $h->settle();
    expect(recordInstance($h)->status)->toBe(InstanceStatus::OUT_OF_SYNC)->and(SlaClocks::read(recordInstance($h)))->toBeEmpty();
    // Back in sync at the state the clock belonged to: a fresh clock, not the paused one.
    $h->store->changeRecord('application', 'a1', (object) ['status' => 'underReview']);
    $h->settle();
    dueIn($h, 5 * DAY);
});

it('resumes a paused clock on an observed move back to its state', function () {
    $h = paused('observe');
    $h->store->changeRecord('application', 'a1', (object) ['status' => 'underReview']);
    $h->settle();

    expect(auditCount($h, 'slaResumed'))->toBe(1);
    dueIn($h, 3 * DAY);
});

it('drops every kept clock when the record reaches a final state', function () {
    $h = paused();
    $h->engine->applyTransition('application', 'a1', 'back', null, reviewer());
    $h->settle();
    $h->engine->signal('payment.received', 'application', 'a1', null);
    expect(SlaClocks::read(recordInstance($h)))->toHaveCount(1);
    $h->engine->applyTransition('application', 'a1', 'approve', null, reviewer());

    expect(SlaClocks::read(recordInstance($h)))->toBeEmpty()->and(timers($h))->toBeEmpty();
});

it('drops a paused clock when the record migrates to another version', function () {
    $h = paused();
    $h->publish(slaLifecycle());
    $h->engine->migrate(recordInstance($h)->id, 2, null, slaAdmin());

    expect(recordInstance($h)->data)->toBe('{}')->and(recordInstance($h)->version)->toBe(2);
});

it('keeps a paused clock paused through archiving and restoring', function () {
    $h = paused();
    $h->engine->archive('lc_s', slaAdmin());
    expect(SlaClocks::read(recordInstance($h)))->toHaveCount(1);
    $h->advance(30 * DAY);
    $h->engine->restore('lc_s', slaAdmin());
    $h->settle();
    expect(timers($h))->toBeEmpty()->and(auditCount($h, 'slaBreached'))->toBe(0);
    // Still paused until the record is back: the 3 days that were left count from the return.
    $h->engine->applyTransition('application', 'a1', 'back', null, reviewer());
    dueIn($h, 3 * DAY);
});

it('restores a running clock shifted by a pause at the same due instant', function () {
    $h = paused();
    $h->advance(10 * DAY);
    $h->engine->applyTransition('application', 'a1', 'back', null, reviewer());
    $due = timers($h)[0]->dueAt;
    $h->engine->archive('lc_s', slaAdmin());
    expect(timers($h))->toBeEmpty();
    $h->advance(DAY);
    $h->engine->restore('lc_s', slaAdmin());

    expect(array_map(fn (BookmarkRecord $b) => $b->dueAt, timers($h)))->toBe([$due])
        ->and(SlaClocks::read(recordInstance($h)))->toBeEmpty();
});

it('clears an incident when it pauses a parked SLA timer, and a resumed timer can be parked, retried and skipped', function () {
    $h = started();
    parkSla($h);
    expect(recordInstance($h)->status)->toBe(InstanceStatus::INCIDENT);
    // An event pause removes the parked timer and with it the incident.
    $h->engine->signal('payment.received', 'application', 'a1', null);
    expect(recordInstance($h)->status)->toBe(InstanceStatus::ACTIVE)->and(recordInstance($h)->error)->toBeNull()->and(timers($h))->toBeEmpty();
    $h->engine->signal('nightly', 'application', 'a1', null);
    // The resumed timer is an ordinary SLA timer: parked, it is retried (re-armed) ...
    parkSla($h);
    $h->engine->retry(recordInstance($h)->id, slaAdmin());
    expect(timers($h))->toHaveCount(1)
        ->and(timers($h)[0]->purpose)->toBe('sla:r')
        ->and(timers($h)[0]->lockedUntil)->toBeNull()
        ->and(recordInstance($h)->status)->toBe(InstanceStatus::ACTIVE);
    // ... or skipped (dropped, the stay's SLA never runs).
    parkSla($h);
    $h->engine->skip(recordInstance($h)->id, null, slaAdmin());
    expect(timers($h))->toBeEmpty()->and(recordInstance($h)->status)->toBe(InstanceStatus::ACTIVE);
});

it('leaves the record instance data untouched for a definition without the pause fields', function () {
    $h = engineHost();
    $h->publish(slaLifecycle(sla: ''));
    $h->store->createRecord('application', 'a1', (object) ['title' => 'A']);
    $h->settle();
    $h->engine->applyTransition('application', 'a1', 'start', null, reviewer());
    $h->engine->applyTransition('application', 'a1', 'ask', null, reviewer());
    $h->engine->archive('lc_s', slaAdmin());
    $h->engine->restore('lc_s', slaAdmin());

    expect(recordInstance($h)->data)->toBe('{}')->and(auditCount($h, 'slaPaused'))->toBe(0);
});
