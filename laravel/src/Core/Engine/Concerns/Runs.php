<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\Exec;
use Zouriel\Workflows\Core\Engine\Frame;
use Zouriel\Workflows\Core\Engine\JoinState;
use Zouriel\Workflows\Core\Engine\RunState;
use Zouriel\Workflows\Core\Engine\SlaClocks;
use Zouriel\Workflows\Core\Engine\Token;
use Zouriel\Workflows\Core\Engine\TriggerInfo;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\ValueScope;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\Assignee;
use Zouriel\Workflows\Core\Model\ForEachStep;
use Zouriel\Workflows\Core\Model\Step;
use Zouriel\Workflows\Core\Model\Template;
use Zouriel\Workflows\Core\Model\TemplateRef;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Storage\BookmarkKinds;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Storage\StepStatus;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\WorkflowException;

/** Starting, driving, finishing and cancelling instances, plus helpers every step uses. Port of .NET `Engine/WorkflowEngine.Run.cs`. */
trait Runs
{
    // ── Starting ──────────────────────────────────────────────────────────

    public function startAutomation(Uow $u, CompiledDefinition $c, ?string $entity, ?string $entityId, TriggerInfo $trigger, WorkflowActor $initiator, int $depth,
        ?stdClass $input, ?string $parentId, ?string $reason, ?string $correlationId, bool $drive = true, ?stdClass $snapshot = null): ?InstanceRecord
    {
        $definition = $c->definition;
        $concurrency = $definition->settings?->concurrency ?? $u->settings->defaultConcurrency();
        $queued = false;
        if ($entityId !== null && $concurrency !== 'parallel') {
            $busy = $u->s->countInstances(new InstanceQuery(definitionId: $c->definitionId, entity: $entity, entityId: $entityId, kinds: [InstanceKinds::AUTOMATION],
                statuses: [InstanceStatus::RUNNING, InstanceStatus::WAITING, InstanceStatus::INCIDENT, InstanceStatus::QUEUED]));
            if ($busy > 0) {
                if ($concurrency === 'skipIfRunning') {
                    $u->audit(new AuditEntry('trigger', 'skipped', 'definition', $c->definitionId, entity: $entity, entityId: $entityId, definitionId: $c->definitionId,
                        data: (object) ['reason' => 'alreadyRunning', 'trigger' => $trigger->toValue()]));

                    return null;
                }
                $queued = true;
            }
        }
        $id = self::newId();
        $state = new RunState;
        $state->input = $input === null ? new stdClass : Json::copy($input);
        $state->snapshot = $snapshot;
        foreach ($definition->variables ?? [] as $v) {
            $state->vars->{$v->key} = Json::normalize($v->default);
        }
        $state->tokens[] = new Token('root', [new Frame('root')]);
        $i = new InstanceRecord;
        $i->id = $id;
        $i->definitionId = $c->definitionId;
        $i->definitionKey = $definition->key;
        $i->version = $c->version;
        $i->kind = InstanceKinds::AUTOMATION;
        $i->parentId = $parentId;
        $i->reason = $reason;
        $i->entity = $entity;
        $i->entityId = $entityId;
        $i->status = $queued ? InstanceStatus::QUEUED : InstanceStatus::RUNNING;
        $i->data = $state->toJson();
        $i->triggerJson = $trigger->toJson();
        $i->depth = $depth;
        $i->initiatorId = $initiator->isUser() ? $initiator->id : null;
        $i->initiatorName = $initiator->isUser() ? $initiator->name : null;
        $i->liveKey = ! $queued && $entityId !== null && $concurrency !== 'parallel' ? InstanceRecord::serialKey($c->definitionId, $entity, $entityId) : null;
        $i->correlationId = $correlationId ?? $id;
        $i->startedAt = $i->updatedAt = $u->now;
        $u->s->addInstance($i);
        $u->audit(new AuditEntry('instance', $queued ? 'queued' : 'started', 'instance', $id, $initiator, $entity, $entityId, $id, $c->definitionId,
            data: (object) ['version' => $c->version, 'trigger' => $trigger->toValue(), 'parent' => $parentId], correlationId: $i->correlationId));
        $u->log?->add('instance', $queued ? 'queued' : 'started', $id, $definition->title);
        $x = $this->track(new Exec($u, $i, $c, $state));
        if (! $queued && $drive) {
            $this->drive($x);
        }

        return $i;
    }

    /**
     * Starts a child run of a lifecycle record (transition steps, onEnter, onExit, SLA, reset); null when the list is
     * empty. `$depth` is the loop protection depth of the move that started it (spec §5).
     */
    public function startRun(Uow $u, InstanceRecord $record, CompiledDefinition $c, string $listId, string $reason, TriggerInfo $trigger, WorkflowActor $initiator,
        int $depth, ?stdClass $input): ?InstanceRecord
    {
        if (($c->lists[$listId] ?? []) === []) {
            return null;
        }
        $id = self::newId();
        $state = new RunState;
        $state->rootList = $listId;
        $state->input = $input === null ? new stdClass : Json::copy($input);
        foreach ($c->definition->variables ?? [] as $v) {
            $state->vars->{$v->key} = Json::normalize($v->default);
        }
        $state->tokens[] = new Token('root', [new Frame($listId)]);
        $i = new InstanceRecord;
        $i->id = $id;
        $i->definitionId = $c->definitionId;
        $i->definitionKey = $c->definition->key;
        $i->version = $c->version;
        $i->kind = InstanceKinds::RUN;
        $i->parentId = $record->id;
        $i->reason = $reason;
        $i->entity = $record->entity;
        $i->entityId = $record->entityId;
        $i->data = $state->toJson();
        $i->triggerJson = $trigger->toJson();
        $i->depth = $depth;
        $i->initiatorId = $initiator->isUser() ? $initiator->id : null;
        $i->initiatorName = $initiator->isUser() ? $initiator->name : null;
        $i->correlationId = $record->correlationId;
        $i->startedAt = $i->updatedAt = $u->now;
        $u->s->addInstance($i);
        $u->audit(new AuditEntry('instance', 'started', 'instance', $id, $initiator, $record->entity, $record->entityId, $id, $c->definitionId,
            data: (object) ['run' => $reason, 'trigger' => $trigger->toValue()], correlationId: $record->correlationId, causationId: $record->id));
        $this->drive($this->track(new Exec($u, $i, $c, $state)));

        return $i;
    }

    private function track(Exec $x): Exec
    {
        return $x->u->execs[$x->i->id] = $x;
    }

    /** The instance as this unit holds it: loaded once, then shared. */
    public function loadExec(Uow $u, string $instanceId): Exec
    {
        if (isset($u->execs[$instanceId])) {
            return $u->execs[$instanceId];
        }
        $i = $u->s->instance($instanceId) ?? throw new WorkflowException('notFound', 'That instance does not exist.');

        return $this->track(new Exec($u, $i, $this->compiled($u, $i->definitionId, $i->version), RunState::fromJson($i->data)));
    }

    // ── Driving ───────────────────────────────────────────────────────────

    /** Runs every active token until all of them wait or finish, then saves the instance. */
    public function drive(Exec $x): void
    {
        // Already executing further up the call stack (a step resumed its own instance): that loop carries on.
        if (isset($x->u->driving[$x->i->id])) {
            return;
        }
        $x->u->driving[$x->i->id] = true;
        try {
            // In incident only because of a parked timer (a failing reminder), a token can still be resumed.
            $wasIncident = $x->i->status === InstanceStatus::INCIDENT;
            if (in_array($x->i->status, [InstanceStatus::RUNNING, InstanceStatus::WAITING], true)) {
                $x->i->status = InstanceStatus::RUNNING;
            }
            $budget = $this->options->maxStepsPerRun;
            while (! $x->ended) {
                $token = array_find($x->s->tokens, fn (Token $t) => $t->status === Token::ACTIVE);
                if ($token === null) {
                    break;
                }
                if (--$budget < 0) {
                    $this->raiseIncident($x, $token, null, 'The workflow ran too many steps without waiting (a loop?). It was stopped.');
                    break;
                }
                $this->stepToken($x, $token);
            }
            if (! $x->ended) {
                $stopped = array_any($x->s->tokens, fn (Token $t) => $t->waiting === 'incident');
                $parked = $wasIncident && ! $stopped ? (self::parkedTimers($x->u, $x->i->id)[0] ?? null) : null;
                if ($stopped) {
                    $x->i->status = InstanceStatus::INCIDENT;
                } elseif ($parked !== null) {
                    $x->i->status = InstanceStatus::INCIDENT;
                    $x->i->error = self::parkedMessage($parked);
                    $x->i->errorStepId = $parked->nodeId;
                } else {
                    // Leaving incident: the error that held it there goes (spec §10).
                    if ($wasIncident) {
                        $x->i->error = null;
                        $x->i->errorStepId = null;
                    }
                    if (array_any($x->s->tokens, fn (Token $t) => $t->status === Token::WAITING)) {
                        $x->i->status = InstanceStatus::WAITING;
                    } elseif (array_all($x->s->tokens, fn (Token $t) => $t->status === Token::DONE || $t->status === Token::CANCELLED)) {
                        $this->finish($x, InstanceStatus::COMPLETED, null);
                    } elseif ($x->i->status === InstanceStatus::INCIDENT) {
                        $x->i->status = InstanceStatus::RUNNING;
                    }
                }
            }
            self::save($x);
        } finally {
            unset($x->u->driving[$x->i->id]);
        }
    }

    public static function save(Exec $x): void
    {
        $x->i->data = $x->s->toJson();
        $x->i->updatedAt = $x->u->now;
        $x->u->s->updateInstance($x->i);
    }

    private function stepToken(Exec $x, Token $t): void
    {
        if ($t->frames === []) {
            $this->tokenFinished($x, $t);

            return;
        }
        $f = $t->top();
        $list = $x->c->list($f->list);
        if ($f->index >= count($list)) {
            array_pop($t->frames);
            $this->listFinished($x, $t, $f);

            return;
        }
        $step = $list[$f->index];
        if ($step->disabled === true) {
            $x->u->audit(self::stepAudit($x, $step, 'skipped', (object) ['reason' => 'disabled']));
            self::advance($t);

            return;
        }
        $this->executeStep($x, $t, $step);
    }

    public static function advance(Token $t): void
    {
        $t->top()->index++;
        $t->top()->attempt = 1;
    }

    /** A nested list ran to its end: continue after the step that holds it. */
    private function listFinished(Exec $x, Token $t, Frame $finished): void
    {
        if ($t->frames === []) {
            $this->tokenFinished($x, $t);

            return;
        }
        $parent = $t->top();
        $container = $x->c->list($parent->list)[$parent->index];
        if ($container instanceof ForEachStep && ! $container->isParallel() && $finished->list === "{$container->id}/each") {
            $key = "{$t->id}:{$container->id}";
            $items = $x->s->loops[$key] ?? null;
            $next = ($finished->itemIndex ?? 0) + 1;
            if ($items !== null && $next < count($items)) {
                $t->frames[] = new Frame("{$container->id}/each", item: $items[$next], itemIndex: $next);

                return;
            }
            unset($x->s->loops[$key]);
            $x->s->setOutput($container->id, 'count', count($items ?? []));
            $this->completeWaitingStep($x, $t, $container);
        }
        self::advance($t);
    }

    private function tokenFinished(Exec $x, Token $t): void
    {
        $t->status = Token::DONE;
        if ($t->parentId === null) {
            return;
        }
        $join = $t->joinKey === null ? null : ($x->s->joins[$t->joinKey] ?? null);
        if ($join === null) {
            return;
        }
        $join->done[] = $t->branch ?? $t->id;
        $parent = $x->s->token($join->tokenId);
        $step = $x->c->step($join->stepId);
        if ($join->kind === 'forEach') {
            $running = count(array_filter($join->children, fn ($id) => $x->s->token($id)?->isLive() === true));
            while ($join->pending !== null && $join->pending !== [] && $running < $join->concurrency) {
                self::spawnItem($x, $parent, $step, $join);
                $running++;
            }
            $satisfied = count($join->done) >= count($join->items ?? []);
            if ($satisfied) {
                $x->s->setOutput($step->id, 'count', count($join->items ?? []));
            }
        } else {
            $satisfied = count($join->done) >= $join->required;
            if ($satisfied) {
                $x->s->setOutput($step->id, 'completed', $join->done);
            }
        }
        if (! $satisfied) {
            return;
        }
        foreach ($join->children as $childId) {
            $child = $x->s->token($childId);
            if ($child?->isLive() === true) {
                $this->cancelToken($x, $child);
            }
        }
        unset($x->s->joins[$t->joinKey]);
        $parent->status = Token::ACTIVE;
        $parent->waiting = null;
        $this->completeWaitingStep($x, $parent, $step);
        self::advance($parent);
    }

    public static function spawnItem(Exec $x, Token $parent, ForEachStep $step, JoinState $join): void
    {
        $index = array_shift($join->pending);
        $child = new Token($x->s->newTokenId(), [new Frame("{$step->id}/each", item: $join->items[$index], itemIndex: $index)], $parent->id, "{$parent->id}:{$step->id}",
            (string) $index);
        $join->children[] = $child->id;
        $x->s->tokens[] = $child;
    }

    /** Stops a token and everything it waits on (timers, tasks, child workflows), its own child tokens included. */
    public function cancelToken(Exec $x, Token $t): void
    {
        foreach ($x->s->tokens as $child) {
            if ($child->parentId === $t->id && $child->isLive()) {
                $this->cancelToken($x, $child);
            }
        }
        $this->clearWaits($x, $t);
        if ($t->stepRecord !== null) {
            $record = array_find($x->u->s->steps($x->i->id), fn (StepRecord $s) => $s->id === $t->stepRecord);
            if ($record !== null && in_array($record->status, [StepStatus::WAITING, StepStatus::RUNNING], true)) {
                $record->status = StepStatus::CANCELLED;
                $record->endedAt = $x->u->now;
                $x->u->s->updateStep($record);
            }
            $t->stepRecord = null;
        }
        $t->status = Token::CANCELLED;
        $t->waiting = null;
    }

    /** Removes a token's bookmarks, cancels its open tasks and its waited-for child instances. */
    public function clearWaits(Exec $x, Token $t): void
    {
        foreach ($x->u->s->bookmarks($x->i->id) as $b) {
            if ($b->tokenId !== $t->id) {
                continue;
            }
            $x->u->s->removeBookmark($b->id);
            if ($b->kind === BookmarkKinds::CHILD && $b->payload !== null) {
                $child = $x->u->s->instance($b->payload);
                if ($child !== null && ! InstanceStatus::isFinished($child->status)) {
                    $this->cancelInstance($x->u, $child, $x->actor(), 'The step that started it was cancelled.');
                }
            }
        }
        foreach ($x->u->s->queryTasks(new TaskQuery(instanceId: $x->i->id, statuses: [TaskStatus::OPEN], take: 1000)) as $task) {
            if ($task->tokenId !== $t->id) {
                continue;
            }
            $task->status = TaskStatus::CANCELLED;
            $task->completedAt = $x->u->now;
            $x->u->s->updateTask($task);
            $x->u->audit(self::taskAudit($task, 'cancelled', $x->actor()));
        }
    }

    // ── Finishing ─────────────────────────────────────────────────────────

    /** Ends an instance: cancels its live tokens, records the outcome and resumes a waiting parent. `$by` defaults to the workflow itself. */
    public function finish(Exec $x, string $status, ?string $error, ?WorkflowActor $by = null): void
    {
        foreach ($x->s->tokens as $t) {
            if ($t->isLive()) {
                $this->cancelToken($x, $t);
            }
        }
        $x->ended = true;
        $x->i->status = $status;
        $x->i->liveKey = null;
        $x->i->error = $error ?? $x->i->error;
        $x->i->endedAt = $x->u->now;
        $x->u->finished[] = $x->i;
        $action = match ($status) {
            InstanceStatus::COMPLETED => 'completed',
            InstanceStatus::FAILED => 'failed',
            default => 'cancelled',
        };
        $x->u->audit(new AuditEntry('instance', $action, 'instance', $x->i->id, $by ?? $x->actor(), $x->i->entity, $x->i->entityId, $x->i->id, $x->i->definitionId,
            data: $error === null ? null : (object) ['reason' => $error], correlationId: $x->i->correlationId));
        $x->u->log?->add('instance', $action, $x->i->id, $error);
        $this->notifyParent($x->u, $x->i, $x->s);
    }

    /** Cancels an instance from outside (an admin, a cancelled parent step, a deleted record). */
    public function cancelInstance(Uow $u, InstanceRecord $instance, WorkflowActor $actor, string $reason): void
    {
        if (InstanceStatus::isFinished($instance->status)) {
            return;
        }
        if ($instance->kind === InstanceKinds::RECORD) {
            $instance = $u->records[$instance->id] ?? $instance;
            foreach ($u->s->queryInstances(new InstanceQuery(parentId: $instance->id, take: 1000)) as $run) {
                if (! InstanceStatus::isFinished($run->status)) {
                    $this->cancelInstance($u, $run, $actor, $reason);
                }
            }
            foreach ($u->s->bookmarks($instance->id) as $b) {
                $u->s->removeBookmark($b->id);
            }
            SlaClocks::clear($instance);
            $instance->status = InstanceStatus::CANCELLED;
            $instance->liveKey = null;
            $instance->endedAt = $instance->updatedAt = $u->now;
            $u->s->updateInstance($instance);
            // A cancelled record instance unpins the record: it is selected again on next use (spec §6 applies to).
            if ($instance->entity !== null && $instance->entityId !== null) {
                $u->pins[] = [$instance->entity, $this->compiled($u, $instance->definitionId, $instance->version)->lifecycle()->field, $instance->entityId, null];
            }
            $u->audit(new AuditEntry('instance', 'cancelled', 'instance', $instance->id, $actor, $instance->entity, $instance->entityId, $instance->id,
                $instance->definitionId, data: (object) ['reason' => $reason], correlationId: $instance->correlationId));

            return;
        }
        $x = $this->loadExec($u, $instance->id);
        $this->finish($x, InstanceStatus::CANCELLED, $reason, $actor);
        self::save($x);
    }

    /** Starts the oldest queued instance for each record whose previous instance just finished. */
    private function startQueuedAfter(Uow $u): void
    {
        // The list grows while this runs: a dequeued instance that finishes at once is looked at too, so the queue drains.
        for ($n = 0; $n < count($u->finished); $n++) {
            $done = $u->finished[$n];
            if ($done->kind !== InstanceKinds::AUTOMATION || $done->entityId === null) {
                continue;
            }
            $queued = $u->s->queryInstances(new InstanceQuery(definitionId: $done->definitionId, entity: $done->entity, entityId: $done->entityId,
                statuses: [InstanceStatus::QUEUED], kinds: [InstanceKinds::AUTOMATION], take: 1000));
            usort($queued, fn (InstanceRecord $a, InstanceRecord $b) => $a->startedAt <=> $b->startedAt);
            if ($queued === []) {
                continue;
            }
            $stillBusy = $u->s->countInstances(new InstanceQuery(definitionId: $done->definitionId, entity: $done->entity, entityId: $done->entityId,
                statuses: [InstanceStatus::RUNNING, InstanceStatus::WAITING, InstanceStatus::INCIDENT], kinds: [InstanceKinds::AUTOMATION]));
            if ($stillBusy > 0) {
                continue;
            }
            $x = $this->loadExec($u, $queued[0]->id);
            $x->i->status = InstanceStatus::RUNNING;
            if ($x->i->entityId !== null && ($x->c->definition->settings?->concurrency ?? $u->settings->defaultConcurrency()) !== 'parallel') {
                $x->i->liveKey = InstanceRecord::serialKey($x->i->definitionId, $x->i->entity, $x->i->entityId);
            }
            $u->audit(new AuditEntry('instance', 'started', 'instance', $x->i->id, WorkflowActor::system(), $x->i->entity, $x->i->entityId, $x->i->id, $x->i->definitionId,
                data: (object) ['fromQueue' => true], correlationId: $x->i->correlationId));
            $this->drive($x);
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    public static function stepAudit(Exec $x, Step $step, string $action, ?stdClass $data = null): AuditEntry
    {
        $merged = (object) ['type' => $step->type, 'title' => $step->title];
        foreach (get_object_vars(Json::normalize($data) ?? new stdClass) as $key => $value) {
            $merged->{$key} = $value;
        }

        return new AuditEntry('step', $action, 'step', $step->id, $x->actor(), $x->i->entity, $x->i->entityId, $x->i->id, $x->i->definitionId, data: $merged,
            correlationId: $x->i->correlationId);
    }

    public static function taskAudit(TaskRecord $t, string $action, WorkflowActor $actor, ?stdClass $data = null): AuditEntry
    {
        return new AuditEntry('task', $action, 'task', $t->id, $actor, $t->entity, $t->entityId, $t->instanceId, data: $data, correlationId: $t->rootInstanceId ?? $t->instanceId);
    }

    public function beginStep(Exec $x, Token $t, Step $step, mixed $input): StepRecord
    {
        $record = new StepRecord;
        $record->id = self::newId();
        $record->instanceId = $x->i->id;
        $record->nodeId = $step->id;
        $record->stepType = $step->type;
        $record->tokenId = $t->id;
        $record->attempt = $t->top()->attempt;
        $record->input = $input === null ? null : Values::serialize($input);
        $record->startedAt = $x->u->now;
        $x->u->s->addStep($record);
        $x->current = $record;
        $x->u->audit(self::stepAudit($x, $step, 'started', $t->top()->attempt > 1 ? (object) ['attempt' => $t->top()->attempt] : null));
        $x->u->log?->add('step', 'started', $step->id, $step->title ?? $step->type);

        return $record;
    }

    public function completeStep(Exec $x, StepRecord $record, Step $step, ?stdClass $outputs, string $status = StepStatus::COMPLETED): void
    {
        if ($x->current === $record) {
            $x->current = null;
        }
        $record->status = $status;
        $record->endedAt = $x->u->now;
        $record->output = $outputs === null ? null : Values::serialize($outputs);
        $x->u->s->updateStep($record);
        $x->u->audit(self::stepAudit($x, $step, $status === StepStatus::COMPLETED ? 'completed' : $status, $outputs === null ? null : (object) ['outputs' => $outputs]));
        $x->u->log?->add('step', $status, $step->id, $step->title ?? $step->type);
    }

    /** Marks the step a token was waiting in as completed, with the outputs gathered so far. */
    public function completeWaitingStep(Exec $x, Token $t, Step $step): void
    {
        if ($t->stepRecord === null) {
            return;
        }
        $record = array_find($x->u->s->steps($x->i->id), fn (StepRecord $s) => $s->id === $t->stepRecord);
        $t->stepRecord = null;
        if ($record !== null) {
            $this->completeStep($x, $record, $step, $x->s->output($step->id));
        }
    }

    public static function wait(Token $t, string $what, ?StepRecord $record): void
    {
        $t->status = Token::WAITING;
        $t->waiting = $what;
        if ($record !== null) {
            $record->status = StepStatus::WAITING;
            $t->stepRecord = $record->id;
        }
    }

    public static function resume(Token $t): void
    {
        $t->status = Token::ACTIVE;
        $t->waiting = null;
    }

    public function isWorking(Exec $x, ?string $calendar): bool
    {
        return ($calendar ?? $x->c->definition->settings?->calendar) === 'working';
    }

    public function due(Exec $x, string $iso, ?string $calendar): int
    {
        return $x->u->calendar->add($x->u->now, IsoDuration::parse($iso), $this->isWorking($x, $calendar));
    }

    public function addTimer(Exec $x, Token $t, Step $step, string $purpose, int $due, ?string $payload, ?string $taskId = null): void
    {
        $b = new BookmarkRecord;
        $b->id = self::newId();
        $b->instanceId = $x->i->id;
        $b->tokenId = $t->id;
        $b->nodeId = $step->id;
        $b->purpose = $purpose;
        $b->dueAt = $due;
        $b->payload = $payload;
        $b->taskId = $taskId;
        $b->entity = $x->i->entity;
        $b->entityId = $x->i->entityId;
        $b->definitionId = $x->i->definitionId;
        $b->createdAt = $x->u->now;
        $x->u->s->addBookmark($b);
    }

    /** Renders a template to text: enums as labels, users as names, booleans as Yes/No; `$encode` is applied to values only. */
    public function render(ValueScope $scope, ?Template $template, ?callable $encode = null): string
    {
        if ($template === null) {
            return '';
        }
        $text = '';
        foreach ($template->parts as $part) {
            if (! $part instanceof TemplateRef) {
                $text .= $part;

                continue;
            }
            $value = $this->renderValue($scope, $part->ref);
            $text .= $encode === null ? $value : $encode($value);
        }

        return $text;
    }

    private function renderValue(ValueScope $scope, string $path): string
    {
        $value = $scope->resolve($path);
        $field = $scope->fieldOf($path);
        if ($field?->type === FieldTypes::ENUM) {
            return $field->optionLabel($value);
        }
        if (($field?->type === FieldTypes::USER || in_array($path, ['trigger.actor', 'instance.initiator'], true)) && $value !== null) {
            return $this->options->identity->user(Values::text($value))?->name ?? Values::text($value);
        }
        if ($field?->type === FieldTypes::RELATION && $value !== null && $field->entity !== null) {
            return $this->recordTitle($scope, $field->entity, Values::text($value)) ?? Values::text($value);
        }
        // Dates read the way people write them (spec §3): "20 Oct 2026"; date-times also get a time and zone.
        if (is_string($value) && ($field?->type === FieldTypes::DATE || $path === 'today') && ($day = Values::formatDay($value)) !== null) {
            return $day;
        }
        if (is_string($value) && ($field?->type === FieldTypes::DATE_TIME || in_array($path, ['now', 'trigger.at', 'instance.startedAt'], true))
            && ($at = Instant::parse($value)) !== null) {
            return Values::formatForPeople($at, $scope->zone);
        }

        return Values::text($value);
    }

    public function recordTitle(ValueScope $scope, string $entity, string $id): ?string
    {
        $titleField = $this->registry->entity($entity)?->titleField;
        if ($titleField === null) {
            return null;
        }
        $snapshot = $scope->load($entity, $id);

        return $snapshot === null ? null : Values::text($snapshot->get($titleField));
    }

    /**
     * Resolves an assignee list to user ids (spec §8), duplicates and blanks dropped.
     *
     * @param  list<Assignee>  $list
     * @param  list<string>|null  $currentAssignees
     * @return list<string>
     */
    public function resolveAssignees(ValueScope $scope, array $list, ?array $currentAssignees, ?string $initiatorId): array
    {
        $ids = [];
        $addValue = function (mixed $v) use (&$ids, &$addValue): void {
            $v = Json::normalize($v);
            match (true) {
                is_string($v) && $v !== '' => $ids[] = $v,
                is_float($v) => $ids[] = Js::numberText($v),
                is_array($v) => array_walk($v, fn ($item) => $addValue($item)),
                $v instanceof stdClass && property_exists($v, 'value') => $addValue($v->value),
                default => null,
            };
        };
        $identity = $this->options->identity;
        foreach ($list as $a) {
            if ($a->role !== null) {
                array_push($ids, ...$identity->usersInRole($a->role));
            } elseif ($a->group !== null) {
                array_push($ids, ...$identity->usersInGroup($a->group));
            } elseif ($a->department !== null) {
                array_push($ids, ...$identity->usersInDepartment($a->department));
            } elseif ($a->portal !== null) {
                array_push($ids, ...$identity->usersInPortal($a->portal));
            } elseif ($a->queue !== null) {
                array_push($ids, ...$identity->usersInQueue($a->queue));
            } elseif ($a->user !== null) {
                $ids[] = $a->user;
            } elseif ($a->field !== null) {
                $addValue($scope->resolve($a->field));
            } elseif ($a->initiator === true && $initiatorId !== null) {
                $ids[] = $initiatorId;
            } elseif ($a->managerOf !== null) {
                $of = [];
                if ($a->managerOf === 'initiator') {
                    if ($initiatorId !== null) {
                        $of[] = $initiatorId;
                    }
                } elseif ($a->managerOf === 'assignees') {
                    $of = $currentAssignees ?? [];
                } else {
                    $before = count($ids);
                    $addValue($scope->resolve($a->managerOf));
                    $of = array_slice($ids, $before);
                    $ids = array_slice($ids, 0, $before);
                }
                foreach ($of as $person) {
                    $manager = $identity->manager($person);
                    if ($manager !== null) {
                        $ids[] = $manager;
                    }
                }
            }
        }

        return array_values(array_unique(array_filter($ids, fn ($id) => $id !== '')));
    }
}
