<?php

namespace Zouriel\Workflows\Core\Testing;

use LogicException;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Engine\EngineOptions;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InMemoryStore;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\ManualClock;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * An engine wired to an in-memory store, a manual clock and recording fakes, to test workflows and actions without a
 * database. Port of .NET `Zouriel.Workflows.Testing/WorkflowTestHost.cs`.
 */
final class WorkflowTestHost
{
    public readonly InMemoryStore $store;

    public readonly ManualClock $clock;

    public readonly FakeIdentity $identity;

    public readonly RecordingNotifications $notifications;

    public readonly FakeWebhooks $webhooks;

    public readonly FakeActions $actions;

    public readonly WorkflowEngine $engine;

    /** When true, settling tolerates timers that fail (tests of backoff and parking); otherwise a failed timer fails the test. */
    public bool $allowTimerFailures = false;

    /** @param  int|null  $now  the clock's start (default 2026-10-19 09:00 UTC) */
    public function __construct(WorkflowRegistry $registry, ?int $now = null)
    {
        $this->clock = new ManualClock($now ?? Instant::parse('2026-10-19T09:00:00Z'));
        $this->store = new InMemoryStore($registry, $this->clock);
        $this->identity = new FakeIdentity;
        $this->notifications = new RecordingNotifications;
        $this->webhooks = new FakeWebhooks;
        $this->actions = new FakeActions;
        $this->engine = new WorkflowEngine(new EngineOptions($registry, $this->store, $this->identity, $this->notifications, $this->webhooks, actions: $this->actions,
            clock: $this->clock, recordLinkTemplate: 'https://app.test/{entity}/{id}'));
        $this->store->changes = $this->engine->changes;
    }

    /** Runs the background work (refused-write audits, outbox, notifications, due timers) until nothing is left, like the worker does. */
    public function settle(): void
    {
        $this->engine->flushBackground();
        for ($i = 0; $i < 100; $i++) {
            $n = $this->engine->processOutbox(500) + $this->engine->processTimers(500);
            // Background failures are engine or test bugs; surface them instead of leaving them parked.
            $failed = array_find($this->store->all('outbox'), fn (OutboxRecord $o) => $o->error !== null);
            if ($failed !== null) {
                throw new LogicException("Outbox {$failed->kind} failed: {$failed->error}");
            }
            $timer = $this->allowTimerFailures ? null : array_find($this->store->all('audit'), fn (AuditRecord $a) => $a->action === 'timerFailed');
            if ($timer !== null) {
                throw new LogicException("Timer failed: {$timer->data}");
            }
            if ($n === 0) {
                return;
            }
        }
        throw new LogicException('Background work did not settle after 100 rounds (a trigger loop?).');
    }

    /** Moves the clock forward (milliseconds) timer by timer, firing timers as they come due, then settles. */
    public function advance(int $by): void
    {
        $end = $this->clock->now() + $by;
        while (true) {
            $this->settle();
            $next = $end;
            foreach ($this->store->all('bookmarks') as $b) {
                /** @var BookmarkRecord $b */
                if ($b->dueAt === null) {
                    continue;
                }
                // A timer backing off after a failure comes due again when its lock runs out (parked ones never do).
                $at = $b->lockedUntil !== null && $b->lockedUntil > $b->dueAt ? $b->lockedUntil : $b->dueAt;
                if ($at > $this->clock->now() && $at <= $end) {
                    $next = min($next, $at);
                }
            }
            $this->clock->set($next);
            $this->settle();
            if ($next >= $end) {
                return;
            }
        }
    }

    /** Creates (or saves a new draft of) a definition and publishes it; returns its id. */
    public function publish(string $json): string
    {
        $admin = WorkflowActor::user('admin', 'Admin');
        $definition = Definitions::parse($json);
        $existing = array_find($this->engine->listDefinitions(), fn (DefinitionSummary $d) => $d->id === $definition->id);
        if ($existing === null) {
            $this->engine->createDefinition($json, $admin);
        } else {
            $this->engine->saveDraft($definition->id, $json, $admin);
        }
        try {
            $this->engine->publish($definition->id, null, $admin);
        } catch (WorkflowException $e) {
            if (! is_array($e->details)) {
                throw $e;
            }
            throw new LogicException("'{$definition->key}' cannot be published: ".implode(' ', array_map(fn (LintProblem $p) => "[{$p->code}] {$p->message}", $e->details)));
        }

        return $definition->id;
    }
}
