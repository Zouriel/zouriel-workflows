<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Throwable;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Changes\ChangeKinds;
use Zouriel\Workflows\Core\Changes\EntityChange;
use Zouriel\Workflows\Core\Changes\LifecycleEvent;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\EventStart;
use Zouriel\Workflows\Core\Engine\Exec;
use Zouriel\Workflows\Core\Engine\Frame;
use Zouriel\Workflows\Core\Engine\Token;
use Zouriel\Workflows\Core\Engine\TriggerInfo;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\ValueScope;
use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Expressions\WorkflowConditions;
use Zouriel\Workflows\Core\Host\NotificationRecipient;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Host\WorkflowNotification;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\Trigger;
use Zouriel\Workflows\Core\Model\TriggerTypes;
use Zouriel\Workflows\Core\Model\WaitStep;
use Zouriel\Workflows\Core\Storage\BookmarkKinds;
use Zouriel\Workflows\Core\Storage\BookmarkPurposes;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\DefinitionStatus;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\OutboxKinds;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;
use Zouriel\Workflows\Core\Storage\WorkflowSession;
use Zouriel\Workflows\Core\Time\Cron;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\WorkflowException;

/** The outbox, change and lifecycle events, timers, schedules, date triggers, signals and manual starts. Port of .NET `Engine/WorkflowEngine.Triggers.cs`. */
trait Triggers
{
    /**
     * Processes committed change and lifecycle events and sends queued notifications; returns how many rows it
     * claimed. Each automation an event starts runs in its own unit of work (a failing one is audited as
     * `trigger.failed`); trigger claims keep a reprocessed event from starting anything twice.
     */
    public function processOutbox(int $batch = 50): int
    {
        $rows = $this->withSession(function (WorkflowSession $s) use ($batch) {
            $rows = $s->claimOutbox($this->now(), $this->now() + $this->claimLockMs, $batch);
            $s->commit();

            return $rows;
        });
        usort($rows, fn (OutboxRecord $a, OutboxRecord $b) => $a->createdAt <=> $b->createdAt ?: strcmp($a->id, $b->id));
        foreach ($rows as $row) {
            // The claim covers the whole batch; once it lapses another worker may take the rest, so stop.
            if ($row->lockedUntil !== null && $this->now() >= $row->lockedUntil) {
                break;
            }
            // One record's events in the order they were written: a newer one waits while an older one is pending.
            if ($row->recordKey !== null && $this->withSession(fn (WorkflowSession $s) => $s->hasEarlierPendingOutbox($row))) {
                $this->deferOutbox($row);

                continue;
            }
            try {
                switch ($row->kind) {
                    case OutboxKinds::NOTIFY:
                        $this->options->notifications->send(WorkflowNotification::fromJson($row->payload));
                        break;
                    case OutboxKinds::CHANGE:
                        $change = EntityChange::fromJson($row->payload);
                        $this->startEach($this->unit(function (Uow $u) use ($change) {
                            $this->processChange($u, $change);

                            return $this->changeStarts($u, $change);
                        }));
                        break;
                    case OutboxKinds::LIFECYCLE:
                        $event = LifecycleEvent::fromJson($row->payload);
                        $this->startEach($this->unit(fn (Uow $u) => $this->lifecycleStarts($u, $event)));
                        break;
                }
                $this->markOutbox($row, null);
            } catch (Throwable $e) {
                $this->markOutbox($row, $e->getMessage());
            }
        }

        return count($rows);
    }

    /** @param  list<EventStart>  $starts */
    private function startEach(array $starts): void
    {
        foreach ($starts as $start) {
            try {
                $this->unit(fn (Uow $u) => $this->tryStartFromEvent($u, $this->compiled($u, $start->definitionId, $start->version), $start));
            } catch (WorkflowConcurrencyException $e) {
                throw $e;
            } catch (Throwable $e) {
                $this->unit(fn (Uow $u) => $u->audit(new AuditEntry('trigger', 'failed', 'definition', $start->definitionId, entity: $start->entity,
                    entityId: $start->entityId, definitionId: $start->definitionId, data: (object) ['eventId' => $start->eventId, 'error' => self::describeError($e)])));
            }
        }
    }

    /** Leaves a row for a later round (an older row of its record is pending); not counted as an attempt. */
    private function deferOutbox(OutboxRecord $row): void
    {
        $row->lockedUntil = $this->now() + 30000;
        $this->withSession(function (WorkflowSession $s) use ($row) {
            $s->updateOutbox($row);
            $s->commit();
        });
    }

    private function markOutbox(OutboxRecord $row, ?string $error): void
    {
        $row->attempts++;
        if ($error === null) {
            $row->processedAt = $this->now();
            $row->lockedUntil = null;
            $row->error = null;
        } else {
            $row->error = $error;
            // Back off 1, 2, 4… minutes, up to an hour; after 10 attempts the row stays parked for an admin.
            $row->lockedUntil = $row->attempts >= 10 ? Instant::MAX : $this->now() + (int) min(60, 2 ** ($row->attempts - 1)) * 60000;
        }
        $this->withSession(function (WorkflowSession $s) use ($row) {
            $s->updateOutbox($row);
            $s->commit();
        });
    }

    /** Deletes processed outbox rows and trigger claims older than the given ages (milliseconds). */
    public function cleanup(int $keepOutboxMs, int $keepClaimsMs): int
    {
        return $this->withSession(function (WorkflowSession $s) use ($keepOutboxMs, $keepClaimsMs) {
            $n = $s->cleanup($this->now() - $keepOutboxMs, $this->now() - $keepClaimsMs);
            $s->commit();

            return $n;
        });
    }

    // ── Change events ─────────────────────────────────────────────────────

    public function processChange(Uow $u, EntityChange $change): void
    {
        // 1. Lifecycles of this entity: per field, the one that applies to this record (spec §6 applies to).
        $lifecycleTriggers = array_values(array_filter($u->s->triggers(), fn ($t) => $t->type === 'lifecycle' && $t->entity === $change->entity));
        if ($change->kind === ChangeKinds::DELETED) {
            // Every lifecycle the record was on ends with it, archived ones included.
            foreach ($this->liveRecordInstances($u, $change->entity, $change->entityId) as $gone) {
                $this->cancelInstance($u, $gone, $change->actor(), 'The record was deleted.');
            }
        }
        // A record keeps the lifecycle it has; one with none yet gets it from its values like the change hook decided
        // ownership: a new record's own values, an existing record's values before this write.
        $values = null;
        if ($change->kind !== ChangeKinds::DELETED && $lifecycleTriggers !== []) {
            $values = $change->kind === ChangeKinds::CREATED
                ? Values::object(array_map(fn ($f) => $f->to, $change->fields))
                : $this->valuesBefore($u, $change);
        }
        $ownedFields = $change->kind === ChangeKinds::DELETED ? [] : array_values(array_unique(array_map(fn ($t) => $t->field, $lifecycleTriggers)));
        foreach ($ownedFields as $ownedField) {
            $c = $this->lifecycleForRecord($u, $change->entity, $change->entityId, $ownedField, $values);
            if ($c === null) {
                continue;
            }
            if ($change->kind === ChangeKinds::CREATED) {
                // A new record enters its first state: onEnter runs and stateEntered automations start (spec §6).
                if (self::findRecordInstance($u, $c->definitionId, $change->entity, $change->entityId) === null) {
                    $this->ensureRecordInstance($u, $c, $change->entity, $change->entityId, $change->actor(), entering: true, depth: $change->depth);
                }

                continue;
            }
            $record = self::findRecordInstance($u, $c->definitionId, $change->entity, $change->entityId);
            $field = $c->lifecycle()->field;
            if (isset($change->fields[$field]) && ! in_array($field, $change->engineFields ?? [], true)) {
                // Enforce mode refuses outside writes before commit; anything that still arrives is observed.
                $this->observeWrite($u, $c, $change, $change->fields[$field]);
            } elseif ($record !== null && in_array($record->status, [InstanceStatus::ACTIVE, InstanceStatus::INCIDENT], true) && $record->version > 0) {
                $this->checkAutoWhen($u, $this->compiled($u, $record->definitionId, $record->version), $record, $change->depth, 0);
            }
        }
        if ($change->kind === ChangeKinds::DELETED) {
            // Waiting automations about a deleted record are cancelled.
            foreach ($u->s->queryInstances(new InstanceQuery(entity: $change->entity, entityId: $change->entityId, kinds: [InstanceKinds::AUTOMATION],
                statuses: [InstanceStatus::WAITING, InstanceStatus::INCIDENT, InstanceStatus::QUEUED], take: 1000)) as $instance) {
                $this->cancelInstance($u, $instance, $change->actor(), 'The record was deleted.');
            }
        } else {
            // 2. Condition waits on this record.
            foreach ($u->s->findBookmarks(BookmarkKinds::CONDITION, $change->entity, $change->entityId, null) as $b) {
                $this->reevaluateConditionWait($u, $b);
            }
        }
        // 3. Date triggers follow changes to their date field (and are dropped with the record).
        $this->planDateTriggers($u, $change);
    }

    /** A changed record's values before the change: its current values with the changed fields put back. */
    private function valuesBefore(Uow $u, EntityChange $change): stdClass
    {
        $values = $u->s->entities()->get($change->entity, $change->entityId)?->values ?? new stdClass;
        foreach ($change->fields as $key => $fc) {
            $values->{$key} = $fc->from;
        }

        return $values;
    }

    /** @return list<EventStart> the automations a change event matches (spec §5 created/fieldChanged/deleted) */
    public function changeStarts(Uow $u, EntityChange $change): array
    {
        $starts = [];
        // A reset taking back what a workflow did starts nothing, so an automation cannot redo it (spec §6).
        if ($change->suppressTriggers) {
            return $starts;
        }
        foreach ($u->s->triggers() as $t) {
            if ($t->entity !== $change->entity || ! in_array($t->type, [TriggerTypes::CREATED, TriggerTypes::FIELD_CHANGED, TriggerTypes::DELETED], true)) {
                continue;
            }
            // A change committed before the workflow was first published belongs to the past (spec §5).
            if ($change->at < $t->publishedAt) {
                continue;
            }
            $trigger = $this->compiled($u, $t->definitionId, $t->version)->definition->trigger;
            $info = new TriggerInfo($trigger->type, actorType: $change->actorType, actorId: $change->actorId, actorName: $change->actorName,
                at: Instant::format($change->at), eventId: $change->eventId);
            if ($trigger->type === TriggerTypes::CREATED && $change->kind === ChangeKinds::CREATED) {
                // matches
            } elseif ($trigger->type === TriggerTypes::DELETED && $change->kind === ChangeKinds::DELETED) {
                // matches
            } elseif ($trigger->type === TriggerTypes::FIELD_CHANGED && $change->kind === ChangeKinds::UPDATED && $trigger->field !== null
                && isset($change->fields[$trigger->field])) {
                $fc = $change->fields[$trigger->field];
                if (Values::deepEquals($fc->from, $fc->to)
                    || ($trigger->to !== null && $trigger->to !== [] && ! array_any($trigger->to, fn ($v) => self::sameValue($v, $fc->to)))
                    || ($trigger->from !== null && $trigger->from !== [] && ! array_any($trigger->from, fn ($v) => self::sameValue($v, $fc->from)))) {
                    continue;
                }
                $info->field = $trigger->field;
                $info->from = $fc->from;
                $info->to = $fc->to;
            } else {
                continue;
            }
            $starts[] = new EventStart($t->definitionId, $t->version, $change->entity, $change->entityId, $change->eventId, $info, $change->actor(), $change->depth,
                $change->causationDefinitionId, $change->kind === ChangeKinds::DELETED ? $change->snapshot : null, $change->correlationId);
        }

        return $starts;
    }

    /** Whether two values are the same as a condition's `eq` sees them. */
    private static function sameValue(mixed $a, mixed $b): bool
    {
        return WorkflowConditions::evaluate((object) ['ref' => '$', 'op' => 'eq', 'value' => (object) ['const' => Values::normalize($a)]],
            fn () => Values::normalize($b), '1970-01-01');
    }

    /** Applies the start rules shared by every event trigger: pause, loop protection, dedupe and the extra condition. */
    private function tryStartFromEvent(Uow $u, CompiledDefinition $c, EventStart $e): bool
    {
        $trigger = $c->definition->trigger;
        $suppressed = fn (string $reason) => new AuditEntry('trigger', $reason === 'paused' ? 'paused' : 'suppressed', 'definition', $c->definitionId,
            entity: $e->entity, entityId: $e->entityId, definitionId: $c->definitionId, data: (object) ['reason' => $reason, 'eventId' => $e->eventId, 'depth' => $e->depth]);
        if ($e->entity !== null && $u->settings->isPaused($e->entity)) {
            $u->audit($suppressed('paused'));

            return false;
        }
        if ($e->depth >= $u->settings->maxChainDepth()) {
            $u->audit($suppressed('maxChainDepth'));

            return false;
        }
        // A workflow's own writes never start the same workflow again unless it opts in (spec §5).
        if ($e->causationDefinitionId === $c->definitionId && $trigger->retrigger !== true) {
            return false;
        }
        if (! $u->s->claimTriggerEvent($c->definitionId, $e->eventId)) {
            return false;
        }
        if ($trigger->when !== null) {
            $scope = new ValueScope($u->s->entities(), $this->registry, $u->now, $u->today, $e->entity, $e->entityId, $e->snapshot, $e->info);
            if (! $scope->condition($trigger->when)) {
                return false;
            }
        }

        return $this->startAutomation($u, $c, $e->entity, $e->entityId, $e->info, $e->actor, $e->depth, null, null, null, $e->correlationId, snapshot: $e->snapshot) !== null;
    }

    /** @return list<EventStart> the automations a lifecycle event matches (spec §5 stateEntered/transitionApplied) */
    public function lifecycleStarts(Uow $u, LifecycleEvent $ev): array
    {
        $starts = [];
        foreach ($u->s->triggers() as $t) {
            if ($t->lifecycle !== $ev->lifecycle || ! in_array($t->type, [TriggerTypes::STATE_ENTERED, TriggerTypes::TRANSITION_APPLIED], true) || $ev->at < $t->publishedAt) {
                continue;
            }
            $trigger = $this->compiled($u, $t->definitionId, $t->version)->definition->trigger;
            if ($trigger->type === TriggerTypes::STATE_ENTERED && ! in_array($ev->toState, $trigger->states ?? [], true)) {
                continue;
            }
            if ($trigger->type === TriggerTypes::TRANSITION_APPLIED && ($ev->transition === null || ! in_array($ev->transition, $trigger->transitions ?? [], true))) {
                continue;
            }
            $info = new TriggerInfo($trigger->type, null, $ev->fromState, $ev->toState, $ev->transition, $ev->actorType, $ev->actorId, $ev->actorName,
                Instant::format($ev->at), $ev->eventId, comment: $ev->comment);
            $starts[] = new EventStart($t->definitionId, $t->version, $ev->entity, $ev->entityId, $ev->eventId, $info, $ev->actor(), $ev->depth, $ev->causationDefinitionId,
                null, $ev->correlationId);
        }

        return $starts;
    }

    private function reevaluateConditionWait(Uow $u, BookmarkRecord $b): void
    {
        if ($b->instanceId === null || $b->tokenId === null) {
            return;
        }
        $x = $this->loadExec($u, $b->instanceId);
        $t = $x->s->token($b->tokenId);
        $w = $x->c->step($b->nodeId);
        if ($t === null || $t->status !== Token::WAITING || ! $w instanceof WaitStep || ! $x->scope($t)->condition($w->until->when)) {
            return;
        }
        $this->clearWaits($x, $t);
        $x->s->setOutput($w->id, 'timedOut', false);
        self::resume($t);
        $this->completeWaitingStep($x, $t, $w);
        self::advance($t);
        $this->drive($x);
    }

    private function planDateTriggers(Uow $u, EntityChange $change): void
    {
        foreach ($u->s->triggers() as $t) {
            if ($t->type !== TriggerTypes::DATE_REACHED || $t->entity !== $change->entity) {
                continue;
            }
            if ($change->kind === ChangeKinds::UPDATED && ($t->field === null || ! isset($change->fields[$t->field]))) {
                continue;
            }
            $c = $this->compiled($u, $t->definitionId, $t->version);
            foreach ($u->s->definitionBookmarks($c->definitionId, BookmarkKinds::DATE_REACHED) as $old) {
                if ($old->entityId === $change->entityId) {
                    $u->s->removeBookmark($old->id);
                }
            }
            if ($change->kind !== ChangeKinds::DELETED) {
                $this->planDateTrigger($u, $c, $change->entity, $change->entityId);
            }
        }
    }

    /** Plans a published dateReached trigger for every existing record, replacing what an earlier version planned (spec §5). */
    public function planDateTriggersForAll(Uow $u, CompiledDefinition $c): void
    {
        foreach ($u->s->definitionBookmarks($c->definitionId, BookmarkKinds::DATE_REACHED) as $old) {
            $u->s->removeBookmark($old->id);
        }
        $entity = $c->definition->entity;
        if ($entity === null) {
            return;
        }
        foreach (self::allIds($u, $entity) as $id) {
            $this->planDateTrigger($u, $c, $entity, $id);
        }
    }

    /** Adds the bookmark for one record's date (the caller removed any earlier one). */
    private function planDateTrigger(Uow $u, CompiledDefinition $c, string $entity, string $id): void
    {
        $trigger = $c->definition->trigger;
        $value = $u->s->entities()->get($entity, $id)?->get($trigger->field);
        $due = self::dateWaitDue($u->calendar, $value, $trigger->offset, $trigger->at);
        // No date, a time already past, or one beyond the calendar's range (never): nothing to plan.
        if ($due === null || $due <= $u->now || $due === Instant::MAX) {
            return;
        }
        $b = new BookmarkRecord;
        $b->id = self::newId();
        $b->kind = BookmarkKinds::DATE_REACHED;
        $b->purpose = BookmarkPurposes::DATE_REACHED;
        $b->definitionId = $c->definitionId;
        $b->entity = $entity;
        $b->entityId = $id;
        $b->dueAt = $due;
        $b->payload = Values::serialize((object) ['version' => $c->version, 'value' => $value]);
        $b->createdAt = $u->now;
        $u->s->addBookmark($b);
    }

    // ── Timers ────────────────────────────────────────────────────────────

    /** Fires due timers: waits, task deadlines, reminders, escalations, retries, automatic transitions, SLAs, schedules and date triggers. */
    public function processTimers(int $batch = 50): int
    {
        $due = $this->withSession(function (WorkflowSession $s) use ($batch) {
            $due = $s->claimDueBookmarks($this->now(), $this->now() + $this->claimLockMs, $batch);
            $s->commit();

            return $due;
        });
        usort($due, fn (BookmarkRecord $a, BookmarkRecord $b) => $a->dueAt <=> $b->dueAt);
        foreach ($due as $b) {
            if ($b->lockedUntil !== null && $this->now() >= $b->lockedUntil) {
                break;
            }
            try {
                $this->unit(fn (Uow $u) => $this->fire($u, $b->id));
            } catch (Throwable $e) {
                $this->unit(fn (Uow $u) => $this->timerFailed($u, $b->id, self::describeError($e)));
            }
        }

        return count($due);
    }

    /**
     * A timer could not be processed (its unit rolled back): count the attempt and back off 1, 2, 4… minutes up to an
     * hour. After `maxTimerAttempts` it is parked for an admin and its instance goes to `incident` (spec §10).
     */
    private function timerFailed(Uow $u, string $bookmarkId, string $error): void
    {
        $b = $u->s->bookmark($bookmarkId);
        if ($b === null) {
            return;
        }
        $b->attempts++;
        $b->error = $error;
        $parked = $b->attempts >= $this->maxTimerAttempts;
        $b->lockedUntil = $parked ? Instant::MAX : $u->now + (int) min(60, 2 ** ($b->attempts - 1)) * 60000;
        $u->s->updateBookmark($b);
        $u->audit(new AuditEntry('instance', 'timerFailed', 'bookmark', $b->id, entity: $b->entity, entityId: $b->entityId, instanceId: $b->instanceId,
            definitionId: $b->definitionId, data: (object) ['purpose' => $b->purpose, 'error' => $error, 'attempt' => $b->attempts, 'parked' => $parked]));
        if (! $parked) {
            return;
        }
        $this->warn("Timer {$b->id} ({$b->purpose}) failed {$b->attempts} times and was parked: {$error}");
        if ($b->instanceId === null) {
            return;
        }
        $message = self::parkedText($b->purpose, $b->attempts, $error);
        $instance = $u->s->instance($b->instanceId);
        if ($instance === null || InstanceStatus::isFinished($instance->status)) {
            return;
        }
        if ($instance->kind === InstanceKinds::RECORD || in_array($b->purpose, [BookmarkPurposes::REMINDER, BookmarkPurposes::ESCALATION], true)) {
            $record = $instance->kind === InstanceKinds::RECORD
                ? ($u->records[$instance->id] ??= $instance)
                : (($u->execs[$instance->id] ?? null)?->i ?? $instance);
            // The record is not frozen: people can still move it (a move replans the state's timers and ends the incident).
            $record->status = InstanceStatus::INCIDENT;
            $record->error = $message;
            $record->errorStepId = $instance->kind === InstanceKinds::RECORD ? null : $b->nodeId;
            $record->updatedAt = $u->now;
            $u->s->updateInstance($record);
            $u->audit(new AuditEntry('instance', 'incident', 'instance', $record->id, entity: $record->entity, entityId: $record->entityId, instanceId: $record->id,
                definitionId: $record->definitionId, data: (object) ['bookmark' => $b->id, 'purpose' => $b->purpose, 'error' => $message], correlationId: $record->correlationId));

            return;
        }
        $x = $this->loadExec($u, $instance->id);
        $t = $b->tokenId === null ? null : $x->s->token($b->tokenId);
        $step = $b->nodeId === null ? null : $x->c->step($b->nodeId);
        if ($t === null || $t->status !== Token::WAITING) {
            return;
        }
        // The token stops in an incident instead; its timers go (a retry runs the step again and plans new ones).
        $this->clearWaits($x, $t);
        $u->s->removeBookmark($b->id);
        $this->raiseIncident($x, $t, $step, $message);
        self::save($x);
    }

    /** Whether a timer was parked after failing too often (locked "for ever"). */
    public static function isParked(BookmarkRecord $b): bool
    {
        return $b->lockedUntil !== null && $b->lockedUntil >= Instant::MAX;
    }

    /** @return list<BookmarkRecord> an instance's parked timers, oldest first */
    public static function parkedTimers(Uow $u, string $instanceId): array
    {
        $parked = array_values(array_filter($u->s->bookmarks($instanceId), self::isParked(...)));
        usort($parked, fn (BookmarkRecord $a, BookmarkRecord $b) => $a->createdAt <=> $b->createdAt);

        return $parked;
    }

    public static function parkedMessage(BookmarkRecord $b): string
    {
        return self::parkedText($b->purpose, $b->attempts, $b->error);
    }

    private static function parkedText(?string $purpose, int $attempts, ?string $error): string
    {
        return "A timer ({$purpose}) failed {$attempts} ".($attempts === 1 ? 'time' : 'times')." and was stopped: {$error}";
    }

    /**
     * An exception as an incident reads it: a workflow refusal's own message; otherwise the innermost cause with
     * its type (.NET `DescribeError`).
     */
    public static function describeError(Throwable $e): string
    {
        if ($e instanceof WorkflowException) {
            return $e->getMessage();
        }
        $inner = $e;
        while (($next = $inner->getPrevious()) !== null && ! $next instanceof WorkflowException) {
            $inner = $next;
        }
        if ($inner->getPrevious() instanceof WorkflowException) {
            return $inner->getPrevious()->getMessage();
        }

        return (new \ReflectionClass($inner))->getShortName().': '.$inner->getMessage();
    }

    /** Fires one due bookmark (it is removed first; reminders and escalations leave the task open). */
    public function fire(Uow $u, string $bookmarkId): void
    {
        $b = $u->s->bookmark($bookmarkId);
        if ($b === null) {
            return;
        }
        $u->s->removeBookmark($b->id);
        switch ($b->purpose) {
            case BookmarkPurposes::SCHEDULE: $this->runSchedule($u, $b);

                return;
            case BookmarkPurposes::DATE_REACHED: $this->runDateTrigger($u, $b);

                return;
            case BookmarkPurposes::REMINDER: $this->taskReminder($u, $b);

                return;
            case BookmarkPurposes::ESCALATION: $this->taskEscalation($u, $b);

                return;
            case BookmarkPurposes::DUE: $this->taskTimedOut($u, $b);

                return;
        }
        if ($b->instanceId === null) {
            return;
        }
        $x = $this->loadExec($u, $b->instanceId);
        if (InstanceStatus::isFinished($x->i->status)) {
            return;
        }
        // A lifecycle archived while the timer waited does nothing to its records (restoring plans the timers again).
        if ($x->i->kind === InstanceKinds::RECORD && $u->s->definition($x->i->definitionId)?->status === DefinitionStatus::ARCHIVED) {
            return;
        }
        if (str_starts_with($b->purpose, BookmarkPurposes::AUTO_PREFIX)) {
            $this->fireAutoTransition($u, $x, substr($b->purpose, strlen(BookmarkPurposes::AUTO_PREFIX)));
        } elseif (str_starts_with($b->purpose, BookmarkPurposes::SLA_PREFIX)) {
            $this->fireSla($u, $x, substr($b->purpose, strlen(BookmarkPurposes::SLA_PREFIX)));
        } else {
            $this->fireTokenTimer($x, $b);
        }
    }

    /** An automatic transition's time came (spec §6 `auto.after`); its guard still applies. */
    private function fireAutoTransition(Uow $u, Exec $x, string $transitionId): void
    {
        $tr = $x->c->transitionById($transitionId);
        if ($tr === null || $x->i->state === null || ! $tr->from->allows($x->c->lifecycle(), $x->i->state, $tr->to)) {
            return;
        }
        try {
            $this->applyTransitionCore($u, $x->c->definition->key, $x->i->entity, $x->i->entityId, $tr->key, new stdClass, WorkflowActor::system(), false, true, 0);
        } catch (WorkflowException $e) {
            if (! in_array($e->errorCode, ['guardFailed', 'notInFromState'], true)) {
                throw $e;
            }
            $u->audit(new AuditEntry('lifecycle', 'autoSkipped', 'record', $x->i->entityId, entity: $x->i->entity, entityId: $x->i->entityId, instanceId: $x->i->id,
                definitionId: $x->i->definitionId, data: (object) ['transition' => $tr->key, 'reason' => $e->errorCode]));
        }
    }

    /** A record stayed in a state longer than its SLA: audit it and run the SLA steps. */
    private function fireSla(Uow $u, Exec $x, string $stateId): void
    {
        $state = $x->c->stateById($stateId);
        if ($state === null || $x->i->state !== $state->id) {
            return;
        }
        $u->audit(new AuditEntry('lifecycle', 'slaBreached', 'record', $x->i->entityId, entity: $x->i->entity, entityId: $x->i->entityId, instanceId: $x->i->id,
            definitionId: $x->i->definitionId, data: (object) ['state' => $state->key, 'within' => $state->sla?->within]));
        $trigger = new TriggerInfo('sla', $x->c->lifecycle()->field, to: $state->key, at: Instant::format($u->now));
        $list = BookmarkPurposes::SLA_PREFIX.$state->id;
        $this->startRun($u, $x->i, $x->c, $list, $list, $trigger, WorkflowActor::system(), 0, null);
    }

    /** A waiting token's timer: a wait ran out, a wait timed out, or a retry is due. */
    private function fireTokenTimer(Exec $x, BookmarkRecord $b): void
    {
        $t = $b->tokenId === null ? null : $x->s->token($b->tokenId);
        if ($t === null || $t->status !== Token::WAITING) {
            return;
        }
        $step = $b->nodeId === null ? null : $x->c->step($b->nodeId);
        if ($step === null) {
            return;
        }
        switch ($b->purpose) {
            case BookmarkPurposes::WAIT:
                $x->s->setOutput($step->id, 'timedOut', false);
                self::resume($t);
                $this->completeWaitingStep($x, $t, $step);
                self::advance($t);
                break;
            case BookmarkPurposes::TIMEOUT:
                $this->clearWaits($x, $t);
                $x->s->setOutput($step->id, 'timedOut', true);
                self::resume($t);
                $this->completeWaitingStep($x, $t, $step);
                if ($step instanceof WaitStep && $step->until->onTimeout !== null) {
                    $t->frames[] = new Frame("{$step->id}/onTimeout");
                } else {
                    self::advance($t);
                }
                break;
            case BookmarkPurposes::RETRY:
                // The step runs again; its step.started entry carries the attempt number.
                self::resume($t);
                break;
            default:
                return;
        }
        $this->drive($x);
    }

    /** Adds the bookmark for a schedule's next run after now (none when the cron never fires again). */
    public function planNextSchedule(Uow $u, string $definitionId, Cron $cron): void
    {
        $next = $cron->next($u->now, $u->calendar);
        if ($next === null) {
            return;
        }
        $b = new BookmarkRecord;
        $b->id = self::newId();
        $b->kind = BookmarkKinds::SCHEDULE;
        $b->purpose = BookmarkPurposes::SCHEDULE;
        $b->definitionId = $definitionId;
        $b->dueAt = $next;
        $b->createdAt = $u->now;
        $u->s->addBookmark($b);
    }

    private function runSchedule(Uow $u, BookmarkRecord $b): void
    {
        $record = $b->definitionId === null ? null : $u->s->definition($b->definitionId);
        if ($record === null || $record->version === 0 || $record->status === DefinitionStatus::ARCHIVED) {
            return;
        }
        $c = $this->compiled($u, $record->id, $record->version);
        $trigger = $c->definition->trigger;
        [$cron] = Cron::tryParse($trigger?->cron);
        if ($trigger?->type !== TriggerTypes::SCHEDULE || $cron === null) {
            return;
        }
        // Plan the next run first, so a failure below doesn't stop the schedule.
        $this->planNextSchedule($u, $record->id, $cron);
        $eventId = 'schedule:'.intdiv($b->dueAt ?? 0, 1000);
        $info = new TriggerInfo(TriggerTypes::SCHEDULE, actorType: 'system', at: Instant::format($u->now), eventId: $eventId);
        if ($trigger->forEach === null) {
            if ($u->s->claimTriggerEvent($c->definitionId, $eventId)) {
                $this->startAutomation($u, $c, null, null, $info, WorkflowActor::system(), 0, null, null, null, null);
            }

            return;
        }
        $entity = $c->definition->entity;
        if ($entity === null || $u->settings->isPaused($entity)) {
            return;
        }
        $limit = max(1, min(500, $trigger->forEach->limit));
        $started = 0;
        foreach (self::allIds($u, $entity) as $id) {
            if ($started >= $limit) {
                break;
            }
            if ($trigger->forEach->when !== null) {
                $scope = new ValueScope($u->s->entities(), $this->registry, $u->now, $u->today, $entity, $id, trigger: $info);
                if (! $scope->condition($trigger->forEach->when)) {
                    continue;
                }
            }
            if (! $u->s->claimTriggerEvent($c->definitionId, "{$eventId}:{$id}")) {
                continue;
            }
            $this->startAutomation($u, $c, $entity, $id, $info, WorkflowActor::system(), 0, null, null, null, null);
            $started++;
        }
    }

    /** @return iterable<string> every id of an entity, read page by page */
    public static function allIds(Uow $u, string $entity): iterable
    {
        $after = null;
        while (true) {
            $ids = $u->s->entities()->listIds($entity, $after, 500);
            yield from $ids;
            if (count($ids) < 500) {
                return;
            }
            $after = $ids[count($ids) - 1];
        }
    }

    private function runDateTrigger(Uow $u, BookmarkRecord $b): void
    {
        $record = $b->definitionId === null ? null : $u->s->definition($b->definitionId);
        if ($record === null || $record->version === 0 || $record->status === DefinitionStatus::ARCHIVED || $b->entity === null || $b->entityId === null) {
            return;
        }
        $c = $this->compiled($u, $record->id, $record->version);
        $trigger = $c->definition->trigger;
        if ($trigger?->type !== TriggerTypes::DATE_REACHED) {
            return;
        }
        $payload = Values::fromJson($b->payload ?? '{}');
        $info = new TriggerInfo(TriggerTypes::DATE_REACHED, $trigger->field, to: $payload->value ?? null, actorType: 'system', at: Instant::format($u->now),
            eventId: "date:{$b->id}");
        $this->tryStartFromEvent($u, $c, new EventStart($c->definitionId, $c->version, $b->entity, $b->entityId, "date:{$b->id}", $info, WorkflowActor::system(), 0,
            null, null, null));
    }

    // ── Signals and manual starts ─────────────────────────────────────────

    /**
     * The app raises a named event (spec §5 `signal`, §7 wait for signal), about one record or about none. Resumes
     * the matching waits and starts matching automations; returns how many waits it resumed plus instances it started.
     */
    public function signal(string $signal, ?string $entity, ?string $entityId, ?stdClass $data, ?WorkflowActor $actor = null): int
    {
        return $this->unit(function (Uow $u) use ($signal, $entity, $entityId, $data, $actor) {
            if ($this->registry->signal($signal) === null) {
                throw new WorkflowException('unknownSignal', "'{$signal}' is not a registered signal.");
            }
            if (($entity === null) !== ($entityId === null)) {
                throw new WorkflowException('noRecord', 'A signal is about a record (entity and id) or about none.');
            }
            $resumed = 0;
            foreach ($u->s->findBookmarks(BookmarkKinds::SIGNAL, $entity, $entityId, $signal) as $b) {
                if ($b->instanceId === null || $b->tokenId === null) {
                    continue;
                }
                $x = $this->loadExec($u, $b->instanceId);
                $t = $x->s->token($b->tokenId);
                $step = $b->nodeId === null ? null : $x->c->step($b->nodeId);
                if ($t === null || $t->status !== Token::WAITING || $step === null) {
                    continue;
                }
                $this->clearWaits($x, $t);
                $x->s->setOutput($step->id, 'timedOut', false);
                $x->s->setOutput($step->id, 'data', $data);
                self::resume($t);
                $this->completeWaitingStep($x, $t, $step);
                self::advance($t);
                $this->drive($x);
                $resumed++;
            }
            $eventId = self::newId();
            $scope = $this->scopes->current();
            foreach ($u->s->triggers() as $t) {
                if ($t->type !== TriggerTypes::SIGNAL || $t->signal !== $signal) {
                    continue;
                }
                $c = $this->compiled($u, $t->definitionId, $t->version);
                if ($c->definition->entity !== $entity) {
                    continue;
                }
                $info = new TriggerInfo(TriggerTypes::SIGNAL, actorType: $actor->type ?? 'system', actorId: $actor?->id, actorName: $actor?->name,
                    at: Instant::format($u->now), eventId: $eventId, data: $data);
                $start = new EventStart($c->definitionId, $c->version, $entity, $entityId, $eventId, $info, $actor ?? WorkflowActor::system(), $scope->depth ?? 0,
                    $scope?->definitionId, null, null);
                if ($this->tryStartFromEvent($u, $c, $start)) {
                    $resumed++;
                }
            }
            if ($entity !== null && $entityId !== null) {
                $this->applySlaSignal($u, $signal, $entity, $entityId, $actor);
            }

            return $resumed;
        });
    }

    /**
     * Whether a person may start a manual trigger: anyone signed in when it names no one, else its `who` (or a role
     * the person carries). "The initiator" is the record's initiator (spec §8), never the person asking.
     */
    public function mayStartManual(Uow $u, Trigger $trigger, WorkflowActor $user, ?string $entity, ?string $entityId): bool
    {
        if (! $user->isUser()) {
            return false;
        }
        if ($trigger->who === null || $trigger->who === []) {
            return true;
        }
        if (array_any($trigger->who, fn ($w) => $w->role !== null && in_array($w->role, $user->roles ?? [], true))) {
            return true;
        }
        $initiator = $entity === null || $entityId === null ? null : $this->recordInitiator($u, $entity, $entityId);

        return in_array($user->id, $this->resolveAssignees($u->scope(null, null, null, null, $entity, $entityId), $trigger->who, null, $initiator), true);
    }

    /** The record's initiator (spec §8): the person its oldest live lifecycle instance names, or null. */
    public function recordInitiator(Uow $u, string $entity, string $id): ?string
    {
        $live = array_values(array_filter($this->liveRecordInstances($u, $entity, $id), fn ($i) => $i->initiatorId !== null));
        usort($live, fn ($a, $b) => $a->startedAt <=> $b->startedAt);

        return $live[0]->initiatorId ?? null;
    }

    /** A person starts a manual-trigger automation, from a record or from none (spec §5 `manual`). */
    public function startManual(string $definitionKey, ?string $entity, ?string $entityId, ?stdClass $input, WorkflowActor $user): InstanceView
    {
        if (! $user->isUser()) {
            throw new WorkflowException('notAllowed', 'Only a signed-in person can start a workflow by hand.');
        }

        return $this->unit(function (Uow $u) use ($definitionKey, $entity, $entityId, $input, $user) {
            $record = $u->s->definitionByKey($definitionKey);
            if ($record === null || $record->version === 0 || $record->status === DefinitionStatus::ARCHIVED) {
                throw new WorkflowException('unknownWorkflow', "There is no published workflow '{$definitionKey}'.");
            }
            $c = $this->compiled($u, $record->id, $record->version);
            $trigger = $c->definition->trigger;
            $title = $c->definition->title;
            if ($trigger?->type !== TriggerTypes::MANUAL) {
                throw new WorkflowException('notManual', "'{$title}' cannot be started by hand.");
            }
            if ($c->definition->entity !== null && ($entity !== $c->definition->entity || $entityId === null)) {
                throw new WorkflowException('noRecord', "'{$title}' starts from a {$c->definition->entity}.");
            }
            if (! $this->mayStartManual($u, $trigger, $user, $entity, $entityId)) {
                throw new WorkflowException('notAllowed', "You are not allowed to start '{$title}'.");
            }
            $input = $this->checkedForm($trigger->form, $input ?? new stdClass, $u);
            $info = new TriggerInfo(TriggerTypes::MANUAL, actorType: $user->type, actorId: $user->id, actorName: $user->name, at: Instant::format($u->now),
                eventId: self::newId(), data: $input);
            $instance = $this->startAutomation($u, $c, $entity, $entityId, $info, $user, 0, $input, null, null, null)
                ?? throw new WorkflowException('alreadyRunning', "'{$title}' is already running for this record.");

            return $this->instanceView($u, $instance->id);
        });
    }

    /** Form answers checked and cleaned with the form's rules (`invalidInput` with the errors); no form keeps nothing. */
    private function checkedForm(?stdClass $form, stdClass $input, Uow $u): stdClass
    {
        if ($form === null) {
            return new stdClass;
        }
        $errors = $this->options->formValidator->validate($form, $input, $u->today);
        if ($errors !== []) {
            throw new WorkflowException('invalidInput', 'Some answers need attention.', $errors);
        }

        return $this->options->formValidator->clean($form, $input, $u->today);
    }

    /** Queues a notification for delivery after commit; a test run only logs it. */
    public function queueNotification(Uow $u, WorkflowNotification $n): void
    {
        if ($u->simulation) {
            $u->log?->add('notify', $n->channel, $n->instanceId, $n->subject.' → '.implode(', ', array_map(fn (NotificationRecipient $r) => $r->name ?? $r->email, $n->to)),
                Values::fromJson($n->toJson()));

            return;
        }
        $row = new OutboxRecord;
        $row->id = OutboxRecord::newId($u->now);
        $row->kind = OutboxKinds::NOTIFY;
        $row->payload = $n->toJson();
        $row->createdAt = $u->now;
        $u->s->addOutbox($row);
    }
}
