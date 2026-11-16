<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditChainVerifier;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Audit\AuditVerification;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\Exec;
use Zouriel\Workflows\Core\Engine\RunState;
use Zouriel\Workflows\Core\Engine\SlaClocks;
use Zouriel\Workflows\Core\Engine\Token;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Engine\Views\ManualOption;
use Zouriel\Workflows\Core\Engine\Views\Page;
use Zouriel\Workflows\Core\Engine\Views\RecordView;
use Zouriel\Workflows\Core\Engine\Views\StepView;
use Zouriel\Workflows\Core\Engine\Views\TaskDecision;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Engine\Views\TokenView;
use Zouriel\Workflows\Core\Engine\Views\TransitionOption;
use Zouriel\Workflows\Core\Engine\Views\TriggerOverview;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\TriggerTypes;
use Zouriel\Workflows\Core\Model\WorkflowDefinition;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Storage\TriggerRecord;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\WorkflowException;

/** What the API and UI read (instances, tasks, records, audit, triggers) and admin interventions (cancel, retry, skip, migrate). Port of .NET `Engine/WorkflowEngine.Views.cs`. */
trait Views
{
    // ── Instances ─────────────────────────────────────────────────────────

    /** One instance with its steps, tokens, outputs, variables and tasks (masked). Throws `notFound`. */
    public function getInstance(string $id): InstanceView
    {
        return $this->unit(fn (Uow $u) => $this->instanceView($u, $id));
    }

    /** @return Page<InstanceView> a page of instances (summaries without steps or outputs) */
    public function queryInstances(InstanceQuery $q): Page
    {
        return $this->unit(fn (Uow $u) => new Page(array_map(fn (InstanceRecord $i) => $this->instanceSummary($u, $i), $u->s->queryInstances($q)), $u->s->countInstances($q)));
    }

    private function instanceSummary(Uow $u, InstanceRecord $i, bool $full = false): InstanceView
    {
        $c = $this->compiled($u, $i->definitionId, $i->version);
        $entityTitle = $i->entity !== null && $i->entityId !== null ? $this->recordTitle($u->scope($i), $i->entity, $i->entityId) : null;
        $state = $c->stateById($i->state);
        // Everything that leaves the engine is masked (spec §11): step data, outputs, variables and the trigger.
        $mask = $u->masker($i->entity, $i->entityId);
        [$steps, $tokens, $tasks, $outputs, $vars] = [null, null, null, null, null];
        if ($full) {
            $steps = array_map(fn (StepRecord $s) => new StepView($s->id, $s->nodeId, $s->stepType, $c->step($s->nodeId)?->title, $s->attempt, $s->status,
                $mask->mask(Values::fromJson($s->input)), $mask->mask(Values::fromJson($s->output)), $mask->maskText($s->error), Instant::format($s->startedAt),
                $s->endedAt === null ? null : Instant::format($s->endedAt)), $u->s->steps($i->id));
            $st = RunState::fromJson($i->data);
            $tokens = array_map(fn (Token $t) => new TokenView($t->id, $t->status, $t->waiting, $t->frames === [] ? null : self::currentStepId($c, $t)), $st->tokens);
            $outputs = $mask->mask($st->outputsValue());
            $vars = $mask->mask($st->vars);
            $tasks = array_map(fn (TaskRecord $t) => $this->taskView($u, $t, null), $u->s->queryTasks(new TaskQuery(instanceId: $i->id, take: 500)));
        }

        return new InstanceView($i->id, $i->definitionId, $i->definitionKey, $c->definition->title, $i->version, $i->kind, $i->parentId, $i->reason, $i->entity, $i->entityId,
            $entityTitle, $i->status, $state?->key, $state?->label, $mask->maskText($i->error), $i->errorStepId, $i->initiatorId, $i->initiatorName,
            Instant::format($i->startedAt), Instant::format($i->updatedAt), $i->endedAt === null ? null : Instant::format($i->endedAt),
            $i->triggerJson === null ? null : $mask->maskTrigger(Values::fromJson($i->triggerJson)), $steps, $tokens, $outputs, $vars, $tasks);
    }

    private static function currentStepId(CompiledDefinition $c, Token $t): ?string
    {
        $f = $t->top();

        return ($c->lists[$f->list][$f->index] ?? null)?->id;
    }

    public function instanceView(Uow $u, string $id): InstanceView
    {
        return $this->instanceSummary($u, $u->s->instance($id) ?? throw new WorkflowException('notFound', 'That instance does not exist.'), true);
    }

    /** The definition version an instance runs, for drawing the run over it. */
    public function getInstanceDefinition(string $id): stdClass
    {
        return $this->unit(function (Uow $u) use ($id) {
            $i = $u->s->instance($id) ?? throw new WorkflowException('notFound', 'That instance does not exist.');

            return Json::decode($u->s->version($i->definitionId, $i->version)->json);
        });
    }

    /** An admin cancels a running or stuck instance. */
    public function cancel(string $id, string $reason, WorkflowActor $admin): InstanceView
    {
        return $this->unit(function (Uow $u) use ($id, $reason, $admin) {
            $i = $u->s->instance($id) ?? throw new WorkflowException('notFound', 'That instance does not exist.');
            if (InstanceStatus::isFinished($i->status)) {
                throw new WorkflowException('finished', 'This instance has already finished.');
            }
            $this->cancelInstance($u, $i, $admin, trim($reason) === '' ? 'Cancelled by an admin.' : $reason);

            return $this->instanceView($u, $id);
        });
    }

    /**
     * Runs one failed step again, or re-arms parked timers (spec §10). `$target` picks the problem by token id, failed
     * step id, or parked timer's bookmark id or purpose (null = the first / all parked timers).
     */
    public function retry(string $id, WorkflowActor $admin, ?string $target = null): InstanceView
    {
        return $this->unit(function (Uow $u) use ($id, $admin, $target) {
            $parked = $this->parkedTargets($u, $id, $target);
            if ($parked !== []) {
                foreach ($parked as $b) {
                    $b->attempts = 0;
                    $b->error = null;
                    $b->lockedUntil = null;
                    $b->dueAt = $u->now;
                    $u->s->updateBookmark($b);
                }
                $this->leaveParkedIncident($u, $id, 'retried', $admin, $parked);

                return $this->instanceView($u, $id);
            }
            [$x, $t] = $this->incident($u, $id, $target);
            $u->audit(self::incidentAudit($x, 'retried', $admin, (object) ['step' => $t->incidentStepId, 'token' => $t->id]));
            $t->top()->attempt = 1;
            $this->resumeIncident($x, $t, false);

            return $this->instanceView($u, $id);
        });
    }

    /** Continues after one failed step (giving its outputs), or drops parked timers (spec §10). `$target` as for `retry`. */
    public function skip(string $id, ?stdClass $outputs, WorkflowActor $admin, ?string $target = null): InstanceView
    {
        return $this->unit(function (Uow $u) use ($id, $outputs, $admin, $target) {
            $parked = $this->parkedTargets($u, $id, $target);
            if ($parked !== []) {
                foreach ($parked as $b) {
                    $u->s->removeBookmark($b->id);
                }
                $this->leaveParkedIncident($u, $id, 'skipped', $admin, $parked);

                return $this->instanceView($u, $id);
            }
            [$x, $t] = $this->incident($u, $id, $target);
            $stepId = $t->incidentStepId;
            if ($stepId !== null) {
                foreach (Values::members($outputs) as $key => $value) {
                    $x->s->setOutput($stepId, $key, $value);
                }
            }
            $u->audit(self::incidentAudit($x, 'skipped', $admin, (object) ['step' => $stepId, 'token' => $t->id, 'outputs' => $outputs]));
            $this->resumeIncident($x, $t, $stepId !== null);

            return $this->instanceView($u, $id);
        });
    }

    /** @return list<BookmarkRecord> the parked timers a retry or skip acts on; empty when the instance is not in incident or has none */
    private function parkedTargets(Uow $u, string $id, ?string $target): array
    {
        $i = $u->s->instance($id) ?? throw new WorkflowException('notFound', 'That instance does not exist.');
        if ($i->status !== InstanceStatus::INCIDENT) {
            return [];
        }
        $parked = self::parkedTimers($u, $id);

        return $target === null ? $parked : array_values(array_filter($parked, fn (BookmarkRecord $b) => $b->id === $target || $b->purpose === $target || $b->nodeId === $target));
    }

    /**
     * After a parked timer was re-armed or dropped: the instance leaves `incident` when nothing else holds it there, back
     * to where it was, and its error is cleared; otherwise it shows the next problem.
     *
     * @param  list<BookmarkRecord>  $timers
     */
    private function leaveParkedIncident(Uow $u, string $id, string $action, WorkflowActor $admin, array $timers): void
    {
        $data = (object) ['timers' => array_map(fn (BookmarkRecord $b) => (object) ['bookmark' => $b->id, 'purpose' => $b->purpose, 'step' => $b->nodeId], $timers)];
        $left = self::parkedTimers($u, $id);
        $instance = $u->s->instance($id) ?? throw new WorkflowException('notFound', 'That instance does not exist.');
        if ($instance->kind === InstanceKinds::RECORD) {
            $record = $u->records[$instance->id] ??= $instance;
            if ($left !== []) {
                $record->error = self::parkedMessage($left[0]);
            } else {
                $record->status = $record->state === null ? InstanceStatus::OUT_OF_SYNC : InstanceStatus::ACTIVE;
                $record->error = null;
                $record->errorStepId = null;
            }
            $record->updatedAt = $u->now;
            $u->s->updateInstance($record);
            $u->audit(new AuditEntry('instance', $action, 'instance', $record->id, $admin, $record->entity, $record->entityId, $record->id, $record->definitionId,
                data: $data, correlationId: $record->correlationId));

            return;
        }
        $x = $this->loadExec($u, $id);
        $u->audit(self::incidentAudit($x, $action, $admin, $data));
        $stopped = array_find($x->s->tokens, fn (Token $o) => $o->waiting === 'incident');
        if ($stopped !== null) {
            $x->i->error = $stopped->incidentError;
            $x->i->errorStepId = $stopped->incidentStepId;
        } elseif ($left !== []) {
            $x->i->error = self::parkedMessage($left[0]);
            $x->i->errorStepId = $left[0]->nodeId;
        } else {
            $x->i->error = null;
            $x->i->errorStepId = null;
            $x->i->status = array_any($x->s->tokens, fn (Token $o) => $o->status === Token::ACTIVE) ? InstanceStatus::RUNNING : InstanceStatus::WAITING;
            if ($x->i->status === InstanceStatus::RUNNING) {
                $this->drive($x);

                return;
            }
        }
        self::save($x);
    }

    /** @return array{0: Exec, 1: Token} */
    private function incident(Uow $u, string $id, ?string $target): array
    {
        $x = $this->loadExec($u, $id);
        if ($x->i->status !== InstanceStatus::INCIDENT) {
            throw new WorkflowException('notInIncident', 'Only a stopped instance can be retried or skipped.');
        }
        $t = array_find($x->s->tokens, fn (Token $t) => $t->waiting === 'incident' && ($target === null || $t->id === $target || $t->incidentStepId === $target))
            ?? throw new WorkflowException('notInIncident', $target === null ? 'Nothing is waiting to be retried.' : "No incident matches '{$target}'.");

        return [$x, $t];
    }

    /** Resumes one incident token; the instance shows the next incident, if any is left, while the token runs on. */
    private function resumeIncident(Exec $x, Token $t, bool $advance): void
    {
        self::resume($t);
        $t->incidentError = null;
        $t->incidentStepId = null;
        if ($advance) {
            self::advance($t);
        }
        $next = array_find($x->s->tokens, fn (Token $o) => $o->waiting === 'incident');
        // A parked timer (a task reminder that keeps failing) keeps the instance in incident too.
        $parked = $next === null ? (self::parkedTimers($x->u, $x->i->id)[0] ?? null) : null;
        $x->i->status = $next === null && $parked === null ? InstanceStatus::RUNNING : InstanceStatus::INCIDENT;
        $x->i->error = $next?->incidentError ?? ($parked === null ? null : self::parkedMessage($parked));
        $x->i->errorStepId = $next?->incidentStepId ?? $parked?->nodeId;
        $this->drive($x);
    }

    private static function incidentAudit(Exec $x, string $action, WorkflowActor $admin, stdClass $data): AuditEntry
    {
        return new AuditEntry('instance', $action, 'instance', $x->i->id, $admin, $x->i->entity, $x->i->entityId, $x->i->id, $x->i->definitionId, data: $data,
            correlationId: $x->i->correlationId);
    }

    /**
     * Moves a lifecycle record (or a waiting automation) to another version. Record states map by key unless `$stateMap`
     * says otherwise (`unmappedState`); an automation's waiting steps must exist in the new version (`cannotMigrate`).
     *
     * @param  array<string, string>|null  $stateMap
     */
    public function migrate(string $id, int $toVersion, ?array $stateMap, WorkflowActor $admin): InstanceView
    {
        return $this->unit(function (Uow $u) use ($id, $toVersion, $stateMap, $admin) {
            $i = $u->s->instance($id) ?? throw new WorkflowException('notFound', 'That instance does not exist.');
            if ($i->kind === InstanceKinds::RECORD) {
                $i = $u->records[$i->id] ??= $i;
            }
            if (InstanceStatus::isFinished($i->status)) {
                throw new WorkflowException('finished', 'A finished instance cannot move to another version.');
            }
            $from = $this->compiled($u, $i->definitionId, $i->version);
            $to = $this->compiled($u, $i->definitionId, $toVersion);
            if ($i->kind === InstanceKinds::RECORD) {
                $this->migrateRecord($u, $i, $from, $to, $stateMap, $admin);
            } else {
                self::ensureSameSteps(RunState::fromJson($i->data), $from, $to);
            }
            $i->version = $toVersion;
            $i->updatedAt = $u->now;
            $u->s->updateInstance($i);
            $u->audit(new AuditEntry('instance', 'migrated', 'instance', $id, $admin, $i->entity, $i->entityId, $id, $i->definitionId, before: (object) ['version' => $from->version],
                after: (object) ['version' => $toVersion], data: (object) ['stateMap' => $stateMap === null ? null : Values::object($stateMap)], correlationId: $i->correlationId));

            return $this->instanceView($u, $id);
        });
    }

    /** @param  array<string, string>|null  $stateMap */
    private function migrateRecord(Uow $u, InstanceRecord $i, CompiledDefinition $from, CompiledDefinition $to, ?array $stateMap, WorkflowActor $admin): void
    {
        $current = $from->stateById($i->state);
        $key = $current === null ? null : ($stateMap[$current->key] ?? $current->key);
        $target = $key === null ? null : $to->stateByKey($key);
        if ($current !== null && $target === null) {
            throw new WorkflowException('unmappedState', "Version {$to->version} has no state for “{$current->label}”. Choose where it should go.");
        }
        if ($target !== null && $target->key !== $current?->key) {
            $this->writeState($u, $to, $i->entity, $i->entityId, $target, $admin, 0, $i->id);
        }
        foreach ($u->s->bookmarks($i->id) as $b) {
            $u->s->removeBookmark($b->id);
        }
        SlaClocks::clear($i);
        $i->state = $target?->id;
        if ($target === null) {
            return;
        }
        // Its timers were all replanned, so a parked one no longer holds it in incident.
        if ($i->status === InstanceStatus::INCIDENT) {
            $i->error = null;
            $i->errorStepId = null;
        }
        $i->status = $target->isTerminal() ? InstanceStatus::COMPLETED
            : (in_array($i->status, [InstanceStatus::OUT_OF_SYNC, InstanceStatus::INCIDENT], true) ? InstanceStatus::ACTIVE : $i->status);
        $i->endedAt = $target->isTerminal() ? $u->now : null;
        if (! $target->isTerminal()) {
            $this->planStateTimers($u, $to, $i, $target);
        }
    }

    /** Every position a live token holds must point at the same step id in the new version. */
    private static function ensureSameSteps(RunState $state, CompiledDefinition $from, CompiledDefinition $to): void
    {
        foreach ($state->tokens as $t) {
            if (! $t->isLive()) {
                continue;
            }
            foreach ($t->frames as $f) {
                $old = $from->lists[$f->list] ?? null;
                $list = $to->lists[$f->list] ?? null;
                $same = $list !== null && $old !== null && ($f->index < count($old)
                    ? $f->index < count($list) && $list[$f->index]->id === $old[$f->index]->id
                    : $f->index >= count($list));
                if (! $same) {
                    throw new WorkflowException('cannotMigrate', "Version {$to->version} no longer has the step this run is at. Cancel it or keep it on version {$from->version}.");
                }
            }
        }
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    /** @return Page<TaskView> a page of tasks; `$viewer` decides `canDecide` */
    public function queryTasks(TaskQuery $q, ?WorkflowActor $viewer): Page
    {
        return $this->unit(fn (Uow $u) => new Page(array_map(fn (TaskRecord $t) => $this->taskView($u, $t, $viewer), $u->s->queryTasks($q)), $u->s->countTasks($q)));
    }

    /** One task as `$viewer` sees it. Throws `notFound`. */
    public function getTask(string $id, ?WorkflowActor $viewer): TaskView
    {
        return $this->unit(fn (Uow $u) => $this->taskView($u, $u->s->task($id) ?? throw new WorkflowException('notFound', 'That task does not exist.'), $viewer));
    }

    public function taskView(Uow $u, TaskRecord $t, ?WorkflowActor $viewer): TaskView
    {
        $assignees = array_map(Values::text(...), Values::fromJson($t->assignees) ?? []);
        $names = array_map(fn (string $a) => $this->options->identity->user($a)?->name ?? $a, $assignees);
        $mask = $u->masker($t->entity, $t->entityId);
        $decisions = array_map(fn (TaskDecision $d) => new TaskDecision($d->userId, $d->name, $d->outcome, $mask->maskText($d->comment),
            $d->data === null ? null : $mask->mask($d->data), $d->at), self::parseDecisions($t));
        $canDecide = $t->status === TaskStatus::OPEN && $viewer?->isUser() === true && in_array($viewer->id, $assignees, true)
            && ! array_any($decisions, fn (TaskDecision $d) => $d->userId === $viewer->id);

        // Titles and instructions are rendered from the record and may quote its sensitive values (spec §11).
        return new TaskView($t->id, $t->instanceId, $t->kind, $mask->maskText($t->title), $mask->maskText($t->instructions), $t->definitionKey, $t->entity, $t->entityId,
            $mask->maskText($t->entityTitle), $t->status, $assignees, $names, Values::fromJson($t->rule), Values::fromJson($t->outcomes) ?? [], $decisions,
            $t->form === null ? null : Json::decode($t->form), $t->requireComment, $t->allowDelegation, $t->outcome, Instant::format($t->dueAt),
            $t->status === TaskStatus::OPEN && $t->dueAt < $u->now, $t->escalationLevel, Instant::format($t->createdAt),
            $t->completedAt === null ? null : Instant::format($t->completedAt), $canDecide);
    }

    // ── Records ───────────────────────────────────────────────────────────

    /** Everything a record page shows: its state, what this person can do, its tasks and runs. */
    public function getRecord(string $entity, string $id, WorkflowActor $viewer): RecordView
    {
        return $this->unit(fn (Uow $u) => $this->recordView($u, $entity, $id, $viewer));
    }

    public function recordView(Uow $u, string $entity, string $id, WorkflowActor $viewer): RecordView
    {
        $descriptor = $this->registry->entity($entity) ?? throw new WorkflowException('unknownEntity', "'{$entity}' is not a workflow record type.");
        $snapshot = $u->s->entities()->get($entity, $id) ?? throw new WorkflowException('noRecord', 'That record does not exist.');
        $title = $descriptor->titleField === null ? null : Values::text($snapshot->get($descriptor->titleField));
        $c = $this->lifecycleForRecord($u, $entity, $id, null, $snapshot->values);
        [$record, $rc, $reset] = [null, null, null];
        $options = [];
        $states = [];
        if ($c !== null) {
            // Reading never writes: a record without a lifecycle instance yet is shown as it would be created.
            $record = self::findRecordInstance($u, $c->definitionId, $entity, $id) ?? self::transientRecordInstance($c, $entity, $id, $snapshot, $u->now);
            $rc = $record->version === $c->version ? $c : $this->compiled($u, $record->definitionId, $record->version);
            $lc = $rc->lifecycle();
            foreach ($lc->states as $s) {
                $states[] = (object) ['key' => $s->key, 'label' => $s->label, 'category' => $s->category, 'tone' => $s->tone];
            }
            if ($record->state !== null) {
                $scope = $u->scope($record);
                foreach ($lc->transitions as $t) {
                    if ($t->hidden === true || ! $t->from->allows($lc, $record->state, $t->to) || ! $this->mayApply($scope, $t, $viewer, $record) || ! $scope->condition($t->when)) {
                        continue;
                    }
                    $to = $rc->stateById($t->to);
                    $options[] = new TransitionOption($t->key, $t->label, $to->key, $to->label, $t->tone, $t->confirm, $t->input?->form === null ? null : Json::copy($t->input->form),
                        $t->resolvedComment());
                }
            }
            $reset = $this->offeredReset($u, $rc, $record, $viewer);
        }
        $tasks = array_map(fn (TaskRecord $t) => $this->taskView($u, $t, $viewer),
            $u->s->queryTasks(new TaskQuery(entity: $entity, entityId: $id, statuses: [TaskStatus::OPEN], take: 100)));
        $instances = array_map(fn (InstanceRecord $i) => $this->instanceSummary($u, $i),
            $u->s->queryInstances(new InstanceQuery(entity: $entity, entityId: $id, kinds: [InstanceKinds::AUTOMATION, InstanceKinds::RUN], take: 50)));
        $manual = [];
        foreach ($u->s->triggers() as $trigger) {
            if ($trigger->type !== TriggerTypes::MANUAL || $trigger->entity !== $entity) {
                continue;
            }
            $mc = $this->compiled($u, $trigger->definitionId, $trigger->version);
            $mt = $mc->definition->trigger;
            if ($this->mayStartManual($u, $mt, $viewer, $entity, $id)) {
                $manual[] = new ManualOption($mc->definition->key, $mt->label ?? $mc->definition->title, $mt->form === null ? null : Json::copy($mt->form));
            }
        }
        $state = $rc?->stateById($record?->state);
        $ppt = ($rc ?? $c)?->definition->publicProcessingTime;

        return new RecordView($entity, $id, $title, $c?->definition->key, $c?->definition->title, $state?->key, $state?->label, $state?->tone, $record?->status,
            $record?->stateEnteredAt === null ? null : Instant::format($record->stateEnteredAt), $options, $tasks, $instances, $manual, $states, $reset,
            $record?->status === InstanceStatus::INCIDENT && $record->error !== null ? $u->masker($entity, $id)->maskText($record->error) : null,
            $ppt !== null && $ppt->display && trim($ppt->estimate) !== '' ? $ppt->estimate : null);
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    /** @return list<AuditRecord> audit entries of this engine's chain matching the query (newest first, or oldest first from `fromSeq`) */
    public function queryAudit(AuditQuery $q): array
    {
        return $this->unit(fn (Uow $u) => $u->s->queryAudit($q->withTenant($this->options->tenant)));
    }

    /** Checks the hash chain from `$fromSeq`, page by page: purge checkpoints first (they explain gaps), then every entry, then the head. */
    public function verifyAudit(int $fromSeq = 1): AuditVerification
    {
        $tenant = $this->options->tenant;
        $purged = [];
        for ($next = 1; ;) {
            $page = $this->unit(fn (Uow $u) => $u->s->queryAudit(new AuditQuery($tenant, 'audit', 'purged', fromSeq: $next, take: 1000)));
            foreach ($page as $p) {
                array_push($purged, ...self::purgedRanges($p));
            }
            if (count($page) < 1000) {
                break;
            }
            $next = $page[count($page) - 1]->seq + 1;
        }
        $verifier = new AuditChainVerifier($purged, $fromSeq);
        for ($next = $fromSeq; ;) {
            $page = $this->unit(fn (Uow $u) => $u->s->queryAudit(new AuditQuery($tenant, fromSeq: $next, take: 1000)));
            foreach ($page as $e) {
                if (! $verifier->add($e)) {
                    return $verifier->result();
                }
            }
            if (count($page) < 1000) {
                break;
            }
            $next = $page[count($page) - 1]->seq + 1;
        }
        $head = $this->options->store->begin()->auditHead($tenant);

        return $verifier->result($head === null ? null : [$head->seq, $head->hash]);
    }

    /** @return list<array{0: int, 1: int}> the exact ranges an `audit.purged` checkpoint recorded */
    private static function purgedRanges(AuditRecord $checkpoint): array
    {
        $data = Values::fromJson($checkpoint->data);
        $ranges = [];
        foreach (is_array($data->ranges ?? null) ? $data->ranges : [] as $r) {
            if (is_array($r) && count($r) === 2 && is_float($r[0]) && is_float($r[1])) {
                $ranges[] = [(int) $r[0], (int) $r[1]];
            }
        }

        return $ranges;
    }

    /** Deletes audit entries older than the retention set in settings, leaving a checkpoint per category with the exact ranges deleted. */
    public function purgeAudit(WorkflowActor $actor): int
    {
        return $this->unit(function (Uow $u) use ($actor) {
            $total = 0;
            foreach ($u->settings->audit->retentionDays ?? [] as $category => $days) {
                if ($days < 1 || $category === 'audit') {
                    continue;
                }
                $before = $u->now - $days * 86400000;
                $result = $u->s->purgeAudit($this->options->tenant, (string) $category, $before);
                if ($result->count === 0) {
                    continue;
                }
                $total += $result->count;
                $u->audit(new AuditEntry('audit', 'purged', 'audit', (string) $category, $actor, data: (object) [
                    'category' => $category, 'before' => Instant::format($before), 'count' => $result->count, 'ranges' => $result->ranges,
                ]));
            }

            return $total;
        });
    }

    /** Records that someone exported audit entries (exports are audited too). */
    public function noteAuditExport(WorkflowActor $actor, string $format, int $count, mixed $filter): void
    {
        $this->unit(fn (Uow $u) => $u->audit(new AuditEntry('audit', 'exported', 'audit', 'export', $actor,
            data: (object) ['format' => $format, 'count' => $count, 'filter' => Json::normalize($filter)])));
    }

    // ── Watching overview (global settings) ───────────────────────────────

    /** @return list<TriggerOverview> every active trigger and lifecycle, for the "Watching" section of global settings */
    public function listTriggers(): array
    {
        return $this->unit(function (Uow $u) {
            $list = [];
            foreach ($u->s->triggers() as $t) {
                $c = $this->compiled($u, $t->definitionId, $t->version);
                // Lifecycle triggers name the lifecycle's states and transitions, so read its published version.
                $lifecycle = null;
                $record = $t->lifecycle === null ? null : $u->s->definitionByKey($t->lifecycle);
                if ($record !== null && $record->version > 0) {
                    $lifecycle = $this->compiled($u, $record->id, $record->version);
                }
                $list[] = new TriggerOverview($t->definitionId, $t->definitionKey, $c->definition->title, $c->definition->kind, $t->type, $t->entity, $t->field,
                    $this->describe($c->definition, $t, $lifecycle), $t->version);
            }

            return $list;
        });
    }

    private function describe(WorkflowDefinition $d, TriggerRecord $t, ?CompiledDefinition $lifecycle): string
    {
        $entity = $this->registry->entity($t->entity);
        $field = $entity?->field($t->field);
        $tr = $d->trigger;
        $values = fn (?array $v) => $v !== null && $v !== [] ? implode(' or ', array_map(fn ($x) => $field?->optionLabel($x) ?? Values::text($x), $v)) : 'anything';
        $a = fn (?string $word) => ($word !== null && $word !== '' && str_contains('aeiouAEIOU', $word[0]) ? 'an' : 'a')." {$word}";
        $lifecycleName = $lifecycle?->definition->title ?? $t->lifecycle ?? 'a lifecycle';

        return match ($t->type) {
            'lifecycle' => 'Controls '.($entity->label ?? $t->entity).' '.($field->label ?? $t->field),
            'created' => 'When '.$a($entity->label ?? $t->entity).' is created',
            'deleted' => 'When '.$a($entity->label ?? $t->entity).' is deleted',
            'fieldChanged' => "When {$entity?->label} ".($field->label ?? $t->field).' changes'.($tr?->from ? ' from '.$values($tr->from) : '').($tr?->to ? ' to '.$values($tr->to) : ''),
            'stateEntered' => "When {$lifecycleName} enters ".implode(' or ', array_map(fn ($k) => $lifecycle?->stateByKey($k)?->label ?? $k, $tr->states ?? [])),
            'transitionApplied' => 'After “'.implode('” or “', array_map(fn ($k) => $lifecycle?->transitionByKey($k)?->label ?? $k, $tr->transitions ?? []))."” in {$lifecycleName}",
            'dateReached' => self::offsetWords($tr?->offset)." {$entity?->label} ".($field->label ?? $t->field),
            'schedule' => "On schedule {$tr?->cron}",
            'manual' => "Button “{$tr?->label}” on ".($entity->label ?? 'nothing'),
            'signal' => "When the app sends {$t->signal}",
            default => $t->type,
        };
    }

    /** "2 days before", "3 hours after", "On" for no offset: a date trigger's offset in words. */
    private static function offsetWords(?string $iso): string
    {
        $d = $iso === null || $iso === '' ? null : IsoDuration::tryParse($iso);
        if ($d === null || $d->isZero()) {
            return 'On';
        }
        $parts = [];
        foreach (['year' => $d->years, 'month' => $d->months, 'week' => $d->weeks, 'day' => $d->days, 'hour' => $d->hours, 'minute' => $d->minutes] as $unit => $n) {
            if ($n !== 0) {
                $parts[] = "{$n} {$unit}".($n === 1 ? '' : 's');
            }
        }

        return implode(' ', $parts).' '.($d->negative ? 'before' : 'after');
    }
}
