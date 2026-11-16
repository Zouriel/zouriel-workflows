<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Changes\EntityChange;
use Zouriel\Workflows\Core\Changes\FieldChange;
use Zouriel\Workflows\Core\Changes\LifecycleEvent;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\LifecycleSelection;
use Zouriel\Workflows\Core\Engine\RunState;
use Zouriel\Workflows\Core\Engine\SlaClock;
use Zouriel\Workflows\Core\Engine\SlaClocks;
use Zouriel\Workflows\Core\Engine\TriggerInfo;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\ValueScope;
use Zouriel\Workflows\Core\Engine\Views\RecordView;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\Assignee;
use Zouriel\Workflows\Core\Model\Condition;
use Zouriel\Workflows\Core\Model\Remarks;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Model\Transition;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\BookmarkPurposes;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\OutboxKinds;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\Storage\TriggerRecord;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\WorkflowException;

/** Lifecycles: record instances, applying transitions, entering and leaving states, timers, observe mode, forced states and resets. Port of .NET `Engine/WorkflowEngine.Lifecycle.cs`. */
trait Lifecycles
{
    /** The published lifecycle of an entity with this key. */
    public function lifecycleFor(Uow $u, string $entity, string $key): ?CompiledDefinition
    {
        foreach ($u->s->triggers() as $t) {
            if ($t->type === 'lifecycle' && $t->entity === $entity && $t->definitionKey === $key) {
                return $this->compiled($u, $t->definitionId, $t->version);
            }
        }

        return null;
    }

    /**
     * The lifecycle a record is on for `$field` (any field when null), spec §6 applies to: the one it has a live record
     * instance of (none while that lifecycle is archived); a record with no instance on the field gets the first
     * lifecycle whose `appliesWhen` its values meet (conditional ones in publish order, then the fallback).
     */
    public function lifecycleForRecord(Uow $u, string $entity, string $id, ?string $field, ?stdClass $values = null): ?CompiledDefinition
    {
        $triggers = array_values(array_filter($u->s->triggers(), fn (TriggerRecord $t) => $t->type === 'lifecycle' && $t->entity === $entity
            && ($field === null || $t->field === $field)));
        $pinnedFields = [];
        foreach ($this->liveRecordInstances($u, $entity, $id) as $i) {
            $f = $this->compiled($u, $i->definitionId, $i->version)->lifecycle()->field;
            if ($field !== null && $f !== $field) {
                continue;
            }
            $own = array_find($triggers, fn (TriggerRecord $t) => $t->definitionId === $i->definitionId);
            if ($own !== null) {
                return $this->compiled($u, $own->definitionId, $own->version);
            }
            $pinnedFields[$f] = true;
        }
        $candidates = array_values(array_filter($triggers, fn (TriggerRecord $t) => ! isset($pinnedFields[$t->field ?? ''])));
        if ($candidates === []) {
            return null;
        }
        $values ??= $u->s->entities()->get($entity, $id)?->values;
        $ordered = self::inSelectionOrder(array_map(fn (TriggerRecord $t) => [$t, self::appliesWhenOf($t)], $candidates), fn ($x) => $x[1] !== null,
            fn ($x) => $x[0]->publishedAt, fn ($x) => $x[0]->definitionId);
        foreach ($ordered as [$t, $when]) {
            if (LifecycleSelection::applies($when, $values, $u->today)) {
                return $this->compiled($u, $t->definitionId, $t->version);
            }
        }

        return null;
    }

    /** @return list<InstanceRecord> the record's lifecycle instances that are not cancelled (archived lifecycles included), as this unit holds them */
    public function liveRecordInstances(Uow $u, string $entity, string $id): array
    {
        $found = $u->s->queryInstances(new InstanceQuery(entity: $entity, entityId: $id, kinds: [InstanceKinds::RECORD], take: 100));
        usort($found, fn (InstanceRecord $a, InstanceRecord $b) => $b->startedAt <=> $a->startedAt);
        $list = [];
        foreach ($found as $f) {
            $i = $u->records[$f->id] ??= $f;
            if ($i->status !== InstanceStatus::CANCELLED) {
                $list[] = $i;
            }
        }
        foreach ($u->records as $held) {
            if ($held->entity === $entity && $held->entityId === $id && $held->status !== InstanceStatus::CANCELLED && ! in_array($held, $list, true)) {
                $list[] = $held;
            }
        }

        return $list;
    }

    /** Refuses (`wrongLifecycle`) a lifecycle named explicitly when it is not the record's own for its field (spec §6). */
    public function ensureOwnLifecycle(Uow $u, CompiledDefinition $c, string $entity, string $id): void
    {
        $own = $this->lifecycleForRecord($u, $entity, $id, $c->lifecycle()->field);
        if ($own?->definitionId !== $c->definitionId) {
            throw new WorkflowException('wrongLifecycle', $own === null
                ? "{$c->definition->title} does not apply to this {$entity}."
                : "{$c->definition->title} does not apply to this {$entity}; it follows {$own->definition->title}.");
        }
    }

    /** The `appliesWhen` a lifecycle's trigger index row carries, or null. */
    public static function appliesWhenOf(TriggerRecord $t): ?Condition
    {
        $data = Values::fromJson($t->data ?? '{}');

        return ($data->appliesWhen ?? null) instanceof stdClass ? Condition::fromJson($data->appliesWhen, '$.appliesWhen') : null;
    }

    public function requireLifecycle(Uow $u, string $key, string $entity): CompiledDefinition
    {
        return $this->lifecycleFor($u, $entity, $key) ?? throw new WorkflowException('unknownLifecycle', "There is no published lifecycle '{$key}' for {$entity}.");
    }

    /**
     * The record's lifecycle instance, created on first use for records that existed before the lifecycle was published.
     * `$entering` is true for a record that was just created: it enters its state like any move (spec §6); a record
     * adopted later only plans its timers. The initiator is the person who created the record, or whose action created
     * the instance (spec §8).
     */
    public function ensureRecordInstance(Uow $u, CompiledDefinition $c, string $entity, string $id, ?WorkflowActor $actor = null, mixed $initialValue = null,
        bool $entering = false, int $depth = 0): InstanceRecord
    {
        $existing = self::findRecordInstance($u, $c->definitionId, $entity, $id);
        if ($existing !== null) {
            return $existing;
        }
        $snapshot = $u->s->entities()->get($entity, $id) ?? throw new WorkflowException('noRecord', "{$entity} {$id} does not exist.");
        $lc = $c->lifecycle();
        $value = $initialValue ?? $snapshot->get($lc->field);
        $state = $c->stateByKey($value);
        $writeInitial = false;
        if ($state === null && Values::isEmpty($value)) {
            $state = $c->initialState();
            $writeInitial = true;
        }
        $i = new InstanceRecord;
        $i->id = self::newId();
        $i->definitionId = $c->definitionId;
        $i->definitionKey = $c->definition->key;
        $i->version = $c->version;
        $i->kind = InstanceKinds::RECORD;
        $i->entity = $entity;
        $i->entityId = $id;
        $i->status = $state === null ? InstanceStatus::OUT_OF_SYNC : ($state->isTerminal() ? InstanceStatus::COMPLETED : InstanceStatus::ACTIVE);
        $i->state = $state?->id;
        $i->stateEnteredAt = $u->now;
        $i->correlationId = self::newId();
        $i->startedAt = $i->updatedAt = $u->now;
        $i->initiatorId = $actor?->isUser() === true ? $actor->id : null;
        $i->initiatorName = $actor?->isUser() === true ? $actor->name : null;
        $i->endedAt = $state?->isTerminal() === true ? $u->now : null;
        $i->liveKey = InstanceRecord::recordKey($c->definitionId, $entity, $id);
        $u->s->addInstance($i);
        $u->records[$i->id] = $i;
        $u->pins[] = [$entity, $lc->field, $id, $c->definitionId];
        $u->audit(new AuditEntry('lifecycle', $state === null ? 'outOfSync' : 'created', 'record', $id, $actor, $entity, $id, $i->id, $c->definitionId,
            after: $state === null ? null : (object) ['state' => $state->key], data: (object) ['version' => $c->version, 'value' => $value], correlationId: $i->correlationId));
        if ($writeInitial) {
            $this->writeState($u, $c, $entity, $id, $state, $actor ?? WorkflowActor::system(), $depth, $i->id);
        }
        if ($entering && $state !== null) {
            $who = $actor ?? WorkflowActor::system();
            $trigger = new TriggerInfo('created', $lc->field, to: $state->key, actorType: $who->type, actorId: $who->id, actorName: $who->name, at: Instant::format($u->now));
            $this->queueLifecycleEvent($u, $c, $entity, $id, null, $state->key, null, $who, $depth, $i->correlationId);
            $this->enterState($u, $c, $i, $state, $who, $trigger, $depth, 0);
        } elseif ($state !== null && ! $state->isTerminal()) {
            $this->planStateTimers($u, $c, $i, $state);
        }

        return $i;
    }

    /** The instance a record would get on first use (spec §6), built for display only and never saved. */
    public static function transientRecordInstance(CompiledDefinition $c, string $entity, string $id, RecordSnapshot $snapshot, int $now): InstanceRecord
    {
        $value = $snapshot->get($c->lifecycle()->field);
        $state = $c->stateByKey($value) ?? (Values::isEmpty($value) ? $c->initialState() : null);
        $i = new InstanceRecord;
        $i->definitionId = $c->definitionId;
        $i->definitionKey = $c->definition->key;
        $i->version = $c->version;
        $i->kind = InstanceKinds::RECORD;
        $i->entity = $entity;
        $i->entityId = $id;
        $i->state = $state?->id;
        $i->status = $state === null ? InstanceStatus::OUT_OF_SYNC : ($state->isTerminal() ? InstanceStatus::COMPLETED : InstanceStatus::ACTIVE);
        $i->startedAt = $i->updatedAt = $now;

        return $i;
    }

    /** The record's lifecycle instance (not cancelled), as this unit holds it, or null when there is none yet. */
    public static function findRecordInstance(Uow $u, string $definitionId, string $entity, string $id): ?InstanceRecord
    {
        $held = array_find($u->records, fn (InstanceRecord $i) => $i->definitionId === $definitionId && $i->entity === $entity && $i->entityId === $id
            && $i->status !== InstanceStatus::CANCELLED);
        if ($held !== null) {
            return $held;
        }
        $found = array_values(array_filter($u->s->queryInstances(new InstanceQuery(definitionId: $definitionId, entity: $entity, entityId: $id, kinds: [InstanceKinds::RECORD],
            take: 10)), fn (InstanceRecord $i) => $i->status !== InstanceStatus::CANCELLED));
        usort($found, fn (InstanceRecord $a, InstanceRecord $b) => $b->startedAt <=> $a->startedAt);
        if ($found === []) {
            return null;
        }

        return $u->records[$found[0]->id] ??= $found[0];
    }

    private function writeState(Uow $u, CompiledDefinition $c, string $entity, string $id, State $state, WorkflowActor $actor, int $depth, string $instanceId): void
    {
        // A workflow step applying the transition stays the cause of the field change, so it does not start itself again.
        $outer = $this->scopes->current();
        $scope = $this->scopes->open($actor, $depth, $outer->instanceId ?? $instanceId, $outer->definitionId ?? $c->definitionId, $outer?->correlationId);
        $scope->allowOwned($entity, $id, $c->lifecycle()->field);
        $this->scopes->within($scope, fn () => $u->s->entities()->setFields($entity, $id, Values::object([$c->lifecycle()->field => $state->key])));
    }

    // ── Applying transitions ──────────────────────────────────────────────

    /**
     * A person applies a transition to a record (spec §6): `who`, the guard and the input are checked and hidden
     * transitions are refused. `$comment` is their remarks (required when the transition's `comment` is `required`).
     */
    public function applyTransition(string $entity, string $id, string $transition, ?stdClass $input, WorkflowActor $user, ?string $lifecycle = null,
        ?string $comment = null): RecordView
    {
        if (! $user->isUser()) {
            throw new WorkflowException('notAllowed', 'Only a signed-in person can apply a transition.');
        }

        return $this->unit(function (Uow $u) use ($entity, $id, $transition, $input, $user, $lifecycle, $comment) {
            $c = $this->lifecycleOf($u, $entity, $id, $lifecycle);
            $this->applyTransitionCore($u, $c->definition->key, $entity, $id, $transition, $input ?? new stdClass, $user, true, false, 0, comment: $comment);

            return $this->recordView($u, $entity, $id, $user);
        });
    }

    /** The app applies a transition itself (like an `applyTransition` step): `who` is not checked, the guard and input are. Trusted code only. */
    public function applyTransitionAsSystem(string $entity, string $id, string $transition, ?stdClass $input = null, ?WorkflowActor $actor = null, ?string $lifecycle = null): void
    {
        $this->unit(function (Uow $u) use ($entity, $id, $transition, $input, $actor, $lifecycle) {
            $c = $this->lifecycleOf($u, $entity, $id, $lifecycle);
            $this->applyTransitionCore($u, $c->definition->key, $entity, $id, $transition, $input ?? new stdClass, $actor ?? WorkflowActor::system(), false, false,
                $this->scopes->current()->depth ?? 0);
        });
    }

    private function lifecycleOf(Uow $u, string $entity, string $id, ?string $lifecycle): CompiledDefinition
    {
        if ($lifecycle === null) {
            return $this->lifecycleForRecord($u, $entity, $id, null) ?? throw new WorkflowException('unknownLifecycle', "No published lifecycle applies to this {$entity}.");
        }
        // A named lifecycle must be the record's own: naming another never starts a second instance (spec §6 applies to).
        $c = $this->requireLifecycle($u, $lifecycle, $entity);
        $this->ensureOwnLifecycle($u, $c, $entity, $id);

        return $c;
    }

    public function applyTransitionCore(Uow $u, string $lifecycleKey, string $entity, string $id, string $transitionKey, stdClass $input, WorkflowActor $actor,
        bool $isPerson, bool $auto, int $depth, int $chain = 0, ?string $comment = null): void
    {
        $c = $this->requireLifecycle($u, $lifecycleKey, $entity);
        $record = $this->ensureRecordInstance($u, $c, $entity, $id, $actor);
        if ($record->version !== $c->version) {
            $c = $this->compiled($u, $record->definitionId, $record->version);
        }
        $lc = $c->lifecycle();
        $tr = $c->transitionByKey($transitionKey) ?? throw new WorkflowException('unknownTransition', "'{$transitionKey}' is not a transition of {$c->definition->title}.");
        if ($record->state === null || ! $tr->from->allows($lc, $record->state, $tr->to)) {
            throw new WorkflowException('notInFromState', "“{$tr->label}” is not possible from the record's current state.");
        }
        $runState = new RunState;
        $runState->input = $input;
        $scope = $u->scope($record, $runState, null, new TriggerInfo('transition', transition: $tr->key));
        if ($isPerson && ($tr->hidden === true || ! $this->mayApply($scope, $tr, $actor, $record))) {
            throw new WorkflowException('notAllowed', "You are not allowed to “{$tr->label}”.");
        }
        if (! $scope->condition($tr->when)) {
            throw new WorkflowException('guardFailed', "“{$tr->label}” is not possible right now.");
        }
        if ($tr->input?->form !== null && ! $auto) {
            // Only the form's shown fields are kept; calculated ones are worked out here, not taken from the request.
            $input = $this->checkedForm($tr->input->form, $input, $u);
        } elseif ($isPerson) {
            $input = new stdClass;
        }
        // Remarks (spec §6): only a person must give required ones; automatic and workflow moves never need them.
        $comment = self::checkRemarks($tr->resolvedComment(), $comment, $isPerson && ! $auto, $tr->label);
        $from = $c->stateById($record->state);
        $to = $c->stateById($tr->to);
        $trigger = new TriggerInfo('transition', $lc->field, $from->key, $to->key, $tr->key, $actor->type, $actor->id, $actor->name, Instant::format($u->now),
            comment: $comment);
        $slaAudits = $this->prepareSlaCarry($u, $c, $record, $from, $to, $actor);
        $this->exitState($u, $c, $record, $from, $actor, $trigger, $depth);
        $this->writeState($u, $c, $entity, $id, $to, $actor, $depth, $record->id);
        // A record created by the app or the system has no initiator yet: the first person who moves it is (spec §8).
        if ($isPerson && $record->initiatorId === null) {
            $record->initiatorId = $actor->id;
            $record->initiatorName = $actor->name;
        }
        $this->moveTo($u, $record, $to);
        $u->audit(new AuditEntry('lifecycle', $auto ? 'autoTransitioned' : 'transitioned', 'record', $id, $actor, $entity, $id, $record->id, $c->definitionId,
            before: (object) ['state' => $from->key], after: (object) ['state' => $to->key],
            data: self::withComment((object) ['transition' => $tr->key, 'label' => $tr->label, 'input' => $input], $comment), correlationId: $record->correlationId));
        foreach ($slaAudits as $a) {
            $u->audit($a);
        }
        $u->log?->add('lifecycle', 'transitioned', $id, "{$from->label} → {$to->label} ({$tr->label})");
        $this->queueLifecycleEvent($u, $c, $entity, $id, $from->key, $to->key, $tr->key, $actor, $depth, $record->correlationId, $comment);
        $this->startRun($u, $record, $c, "transition:{$tr->id}", "transition:{$tr->id}", $trigger, $actor, $depth, $input);
        // The transition's own steps may have moved the record on already; then that state was entered instead.
        if ($record->state === $to->id) {
            $this->enterState($u, $c, $record, $to, $actor, $trigger, $depth, $chain, $input);
        }
    }

    /** A record instance now in `$to`: entered now, active or completed, out of any incident its old timers held it in. */
    private function moveTo(Uow $u, InstanceRecord $record, State $to): void
    {
        $record->state = $to->id;
        $record->stateEnteredAt = $u->now;
        self::movedOutOfIncident($record);
        $record->status = $to->isTerminal() ? InstanceStatus::COMPLETED : InstanceStatus::ACTIVE;
        $record->endedAt = $to->isTerminal() ? $u->now : null;
        $record->updatedAt = $u->now;
        $u->s->updateInstance($record);
    }

    /**
     * The remarks to keep for a move or reset (spec §6): trimmed, null when empty or when the setting is `none`. A person
     * must give them when `required` (`commentRequired`) and may give at most 2000 characters (`invalidInput`); a
     * workflow's are cut to that length.
     */
    public static function checkRemarks(string $setting, ?string $comment, bool $byPerson, string $label): ?string
    {
        if ($setting === Remarks::NONE) {
            return null;
        }
        $text = $comment === null || Js::trim($comment) === '' ? null : Js::trim($comment);
        if ($text === null && $byPerson && $setting === Remarks::REQUIRED) {
            throw new WorkflowException('commentRequired', "Add remarks to explain why you “{$label}”.");
        }
        if ($text !== null && Js::length($text) > Remarks::MAX_LENGTH) {
            if ($byPerson) {
                throw new WorkflowException('invalidInput', 'Remarks can be at most '.Remarks::MAX_LENGTH.' characters.');
            }
            $text = mb_substr($text, 0, Remarks::MAX_LENGTH);
        }

        return $text;
    }

    /** A record instance moved: its state's timers were replanned, so a parked timer no longer holds it in `incident` (spec §10). */
    private static function movedOutOfIncident(InstanceRecord $record): void
    {
        if ($record->status === InstanceStatus::INCIDENT) {
            $record->error = null;
            $record->errorStepId = null;
        }
    }

    private static function withComment(stdClass $data, ?string $comment): stdClass
    {
        if ($comment !== null) {
            $data->comment = $comment;
        }

        return $data;
    }

    /** Whether a person may apply a transition by hand: they are among its `who`, "the initiator" being the record's (spec §8). */
    public function mayApply(ValueScope $scope, Transition $tr, WorkflowActor $actor, InstanceRecord $record): bool
    {
        return $this->isAmong($scope, $tr->who, $actor, $record->initiatorId);
    }

    /** Whether a signed-in person is one of `$who` (empty = nobody); roles the caller carries count too. @param  list<Assignee>|null  $who */
    private function isAmong(ValueScope $scope, ?array $who, WorkflowActor $actor, ?string $initiatorId): bool
    {
        if (! $actor->isUser() || $who === null || $who === []) {
            return false;
        }
        if (in_array($actor->id, $this->resolveAssignees($scope, $who, null, $initiatorId), true)) {
            return true;
        }

        return array_any($who, fn (Assignee $w) => $w->role !== null && in_array($w->role, $actor->roles ?? [], true));
    }

    private function exitState(Uow $u, CompiledDefinition $c, InstanceRecord $record, State $from, WorkflowActor $actor, TriggerInfo $trigger, int $depth): void
    {
        foreach ($u->s->bookmarks($record->id) as $b) {
            if (str_starts_with($b->purpose, BookmarkPurposes::AUTO_PREFIX) || str_starts_with($b->purpose, BookmarkPurposes::SLA_PREFIX)) {
                $u->s->removeBookmark($b->id);
            }
        }
        if ($from->keepRunsOnExit !== true) {
            $this->cancelStateRuns($u, $record, $from);
        }
        $this->startRun($u, $record, $c, "onExit:{$from->id}", "onExit:{$from->id}", $trigger, $actor, $depth, null);
    }

    /** Cancels the onEnter and SLA runs a state started (the record left it); a run executing right now is the one moving it. */
    private function cancelStateRuns(Uow $u, InstanceRecord $record, State $state): void
    {
        foreach ($u->s->queryInstances(new InstanceQuery(kinds: [InstanceKinds::RUN], parentId: $record->id, take: 1000)) as $run) {
            if (InstanceStatus::isFinished($run->status) || isset($u->driving[$run->id])) {
                continue;
            }
            if ($run->reason === "onEnter:{$state->id}" || $run->reason === "sla:{$state->id}") {
                $this->cancelInstance($u, $run, WorkflowActor::system(), "The record left “{$state->label}”.");
            }
        }
    }

    private function enterState(Uow $u, CompiledDefinition $c, InstanceRecord $record, State $to, WorkflowActor $actor, TriggerInfo $trigger, int $depth, int $chain,
        ?stdClass $input = null): void
    {
        // onEnter steps can read the answers of the transition that entered the state (input.*).
        $this->startRun($u, $record, $c, "onEnter:{$to->id}", "onEnter:{$to->id}", $trigger, $actor, $depth, $input);
        // The onEnter steps may already have moved the record on; then the new state planned its own timers.
        if ($to->isTerminal() || $record->state !== $to->id) {
            return;
        }
        $this->planStateTimers($u, $c, $record, $to, actor: $actor);
        $this->checkAutoWhen($u, $c, $record, $depth, $chain);
    }

    /** Plans a state's automatic-transition and SLA timers, counted from `$from` (default now). */
    private function planStateTimers(Uow $u, CompiledDefinition $c, InstanceRecord $record, State $state, ?int $from = null, bool $withSla = true,
        ?WorkflowActor $actor = null): void
    {
        $start = $from ?? $u->now;
        $lc = $c->lifecycle();
        foreach ($lc->transitions as $t) {
            $after = $t->auto?->after === null ? null : IsoDuration::tryParse($t->auto->after);
            if ($after === null || ! $t->from->allows($lc, $state->id, $t->to)) {
                continue;
            }
            $working = ($t->auto->calendar ?? $c->definition->settings?->calendar) === 'working';
            $u->s->addBookmark($this->recordTimer($u, $record, BookmarkPurposes::AUTO_PREFIX.$t->id, $u->calendar->add($start, $after, $working)));
        }
        $within = $state->sla === null ? null : IsoDuration::tryParse($state->sla->within);
        if ($withSla && $within !== null) {
            if ($this->planKeptSla($u, $c, $record, $state, $actor, 'status')) {
                return;
            }
            $working = ($state->sla->calendar ?? $c->definition->settings?->calendar) === 'working';
            $u->s->addBookmark($this->recordTimer($u, $record, BookmarkPurposes::SLA_PREFIX.$state->id, $u->calendar->add($start, $within, $working)));
        }
    }

    private function recordTimer(Uow $u, InstanceRecord $record, string $purpose, int $due): BookmarkRecord
    {
        $b = new BookmarkRecord;
        $b->id = self::newId();
        $b->instanceId = $record->id;
        $b->purpose = $purpose;
        $b->dueAt = $due;
        $b->entity = $record->entity;
        $b->entityId = $record->entityId;
        $b->definitionId = $record->definitionId;
        $b->createdAt = $u->now;

        return $b;
    }

    /**
     * Archiving a lifecycle drops its records' automatic-transition and SLA timers; restoring plans them again, counted
     * from when each record entered its state (an SLA that already ran is not planned again).
     */
    public function archiveRecordTimers(Uow $u, string $definitionId, bool $restore): void
    {
        for ($skip = 0; ; $skip += 500) {
            $page = $u->s->queryInstances(new InstanceQuery(definitionId: $definitionId, kinds: [InstanceKinds::RECORD],
                statuses: [InstanceStatus::ACTIVE, InstanceStatus::INCIDENT], skip: $skip, take: 500));
            foreach ($page as $found) {
                $record = $u->records[$found->id] ??= $found;
                $timers = array_values(array_filter($u->s->bookmarks($record->id), fn (BookmarkRecord $b) => str_starts_with($b->purpose, BookmarkPurposes::AUTO_PREFIX)
                    || str_starts_with($b->purpose, BookmarkPurposes::SLA_PREFIX)));
                foreach ($timers as $b) {
                    $u->s->removeBookmark($b->id);
                }
                if (! $restore) {
                    $this->keepRunningSla($u, $record, $timers);
                }
                if ($record->status === InstanceStatus::INCIDENT && array_any($timers, self::isParked(...))) {
                    // The parked timer went with the others: nothing holds the record in incident any more.
                    self::movedOutOfIncident($record);
                    $record->status = InstanceStatus::ACTIVE;
                    $record->updatedAt = $u->now;
                    $u->s->updateInstance($record);
                }
                if (! $restore) {
                    continue;
                }
                $c = $this->compiled($u, $record->definitionId, $record->version);
                $state = $c->stateById($record->state);
                if ($state === null || $state->isTerminal()) {
                    continue;
                }
                $entered = $record->stateEnteredAt ?? $u->now;
                $slaRan = array_any($u->s->queryInstances(new InstanceQuery(kinds: [InstanceKinds::RUN], parentId: $record->id, take: 1000)),
                    fn (InstanceRecord $r) => $r->reason === BookmarkPurposes::SLA_PREFIX.$state->id && $r->startedAt >= $entered)
                    || $u->s->queryAudit(new AuditQuery($this->options->tenant, 'lifecycle', 'slaBreached', instanceId: $record->id, from: $entered, take: 1)) !== [];
                $this->planStateTimers($u, $c, $record, $state, $entered, ! $slaRan);
            }
            if (count($page) < 500) {
                return;
            }
        }
    }

    /** Applies the first automatic transition whose condition holds in the current state. */
    public function checkAutoWhen(Uow $u, CompiledDefinition $c, InstanceRecord $record, int $depth, int $chain): void
    {
        if ($record->state === null || $chain >= 20) {
            return;
        }
        $lc = $c->lifecycle();
        foreach ($lc->transitions as $t) {
            if ($t->auto?->when === null || ! $t->from->allows($lc, $record->state, $t->to)) {
                continue;
            }
            $scope = $u->scope($record);
            if (! $scope->condition($t->auto->when) || ! $scope->condition($t->when)) {
                continue;
            }
            $this->applyTransitionCore($u, $c->definition->key, $record->entity, $record->entityId, $t->key, new stdClass, WorkflowActor::system(), false, true, $depth,
                $chain + 1);

            return;
        }
    }

    private function queueLifecycleEvent(Uow $u, CompiledDefinition $c, string $entity, string $id, ?string $from, string $to, ?string $transition, WorkflowActor $actor,
        int $depth, string $correlation, ?string $comment = null): void
    {
        if ($u->simulation) {
            return;
        }
        $ev = new LifecycleEvent;
        $ev->lifecycle = $c->definition->key;
        $ev->entity = $entity;
        $ev->entityId = $id;
        $ev->fromState = $from;
        $ev->toState = $to;
        $ev->transition = $transition;
        $ev->actorType = $actor->type;
        $ev->actorId = $actor->id;
        $ev->actorName = $actor->name;
        $ev->depth = $depth;
        $ev->correlationId = $correlation;
        $ev->at = $u->now;
        $ev->causationDefinitionId = $this->scopes->current()?->definitionId;
        $ev->comment = $comment;
        $row = new OutboxRecord;
        $row->id = OutboxRecord::newId($u->now);
        $row->kind = OutboxKinds::LIFECYCLE;
        $row->payload = $ev->toJson();
        $row->createdAt = $u->now;
        $row->recordKey = OutboxRecord::keyOf($entity, $id);
        $u->s->addOutbox($row);
    }

    /** Puts an out-of-sync record instance back in sync at a state (no steps run; its timers are planned). */
    private function followState(Uow $u, CompiledDefinition $c, InstanceRecord $record, State $state): void
    {
        if ($record->state !== $state->id) {
            $record->stateEnteredAt = $u->now;
        }
        SlaClocks::clear($record);
        $record->state = $state->id;
        self::movedOutOfIncident($record);
        $record->status = $state->isTerminal() ? InstanceStatus::COMPLETED : InstanceStatus::ACTIVE;
        $record->endedAt = $state->isTerminal() ? $u->now : null;
        $record->updatedAt = $u->now;
        $u->s->updateInstance($record);
        if (! $state->isTerminal()) {
            $this->planStateTimers($u, $c, $record, $state);
        }
    }

    /** The record's value matches no state a transition leads to: stop the state's runs and timers and wait for a valid value. */
    private function markOutOfSync(Uow $u, CompiledDefinition $c, InstanceRecord $record, ?State $state, mixed $value): void
    {
        foreach ($u->s->bookmarks($record->id) as $b) {
            $u->s->removeBookmark($b->id);
        }
        SlaClocks::clear($record);
        if ($state !== null && $state->keepRunsOnExit !== true) {
            $this->cancelStateRuns($u, $record, $state);
        }
        $record->status = InstanceStatus::OUT_OF_SYNC;
        $record->updatedAt = $u->now;
        $u->s->updateInstance($record);
        $u->audit(new AuditEntry('lifecycle', 'outOfSync', 'record', $record->entityId, entity: $record->entity, entityId: $record->entityId, instanceId: $record->id,
            definitionId: $c->definitionId, data: (object) ['value' => $value], correlationId: $record->correlationId));
    }

    /** An admin moves a record to any state, skipping checks (audited with the reason). */
    public function forceState(string $entity, string $id, string $stateKey, string $reason, bool $skipSteps, WorkflowActor $admin, ?string $lifecycle = null): RecordView
    {
        return $this->unit(function (Uow $u) use ($entity, $id, $stateKey, $reason, $skipSteps, $admin, $lifecycle) {
            if (trim($reason) === '') {
                throw new WorkflowException('reasonRequired', 'Say why the state is being changed.');
            }
            $c = $this->lifecycleOf($u, $entity, $id, $lifecycle);
            $record = $this->ensureRecordInstance($u, $c, $entity, $id, $admin);
            if ($record->version !== $c->version) {
                $c = $this->compiled($u, $record->definitionId, $record->version);
            }
            $to = $c->stateByKey($stateKey) ?? throw new WorkflowException('unknownState', "'{$stateKey}' is not a state of {$c->definition->title}.");
            $from = $c->stateById($record->state);
            $trigger = new TriggerInfo('forced', $c->lifecycle()->field, $from?->key, $to->key, actorType: $admin->type, actorId: $admin->id, actorName: $admin->name,
                at: Instant::format($u->now));
            if ($from !== null && ! $skipSteps) {
                $this->exitState($u, $c, $record, $from, $admin, $trigger, 0);
            } else {
                foreach ($u->s->bookmarks($record->id) as $b) {
                    $u->s->removeBookmark($b->id);
                }
                // Skipping steps skips onExit/onEnter, but the record still leaves the state: its onEnter and SLA runs stop.
                if ($from !== null && $from->keepRunsOnExit !== true) {
                    $this->cancelStateRuns($u, $record, $from);
                }
            }
            $this->writeState($u, $c, $entity, $id, $to, $admin, 0, $record->id);
            SlaClocks::clear($record);
            $this->moveTo($u, $record, $to);
            $u->audit(new AuditEntry('lifecycle', 'forced', 'record', $id, $admin, $entity, $id, $record->id, $c->definitionId, before: (object) ['state' => $from?->key],
                after: (object) ['state' => $to->key], data: (object) ['reason' => $reason, 'skipSteps' => $skipSteps], correlationId: $record->correlationId));
            $this->queueLifecycleEvent($u, $c, $entity, $id, $from?->key, $to->key, null, $admin, 0, $record->correlationId);
            if (! $skipSteps) {
                $this->enterState($u, $c, $record, $to, $admin, $trigger, 0, 0);
            } elseif (! $to->isTerminal()) {
                $this->planStateTimers($u, $c, $record, $to);
            }

            return $this->recordView($u, $entity, $id, $admin);
        });
    }

    /**
     * Observe mode (spec §6): an outside write moved the field. It is recorded as the transition from the state the
     * lifecycle agrees with to the new value when one exists (its steps run); otherwise the instance becomes `outOfSync`
     * until the value returns to that state or a write reaches a state a transition leads to.
     */
    public function observeWrite(Uow $u, CompiledDefinition $c, EntityChange $change, FieldChange $fc): void
    {
        // A record that existed before the lifecycle starts from the value it had before this write.
        $record = $this->ensureRecordInstance($u, $c, $change->entity, $change->entityId, initialValue: $fc->from);
        if ($record->version !== $c->version) {
            $c = $this->compiled($u, $record->definitionId, $record->version);
        }
        $lc = $c->lifecycle();
        $to = $c->stateByKey($fc->to);
        $from = $c->stateById($record->state);
        $tr = $to === null || $from === null ? null : array_find($lc->transitions, fn (Transition $t) => $t->to === $to->id && $t->from->allows($lc, $from->id, $t->to));
        if ($to !== null && $from?->id === $to->id && $record->status !== InstanceStatus::OUT_OF_SYNC) {
            return;
        }
        $u->audit(new AuditEntry('lifecycle', 'writeObserved', 'record', $change->entityId, $change->actor(), $change->entity, $change->entityId, $record->id, $c->definitionId,
            before: (object) ['state' => $fc->from], after: (object) ['state' => $fc->to], data: (object) ['transition' => $tr?->key], correlationId: $record->correlationId));
        if ($to !== null && ($from?->id === $to->id || $from === null)) {
            // Back in the state the lifecycle agrees with, or out of sync on a value no state matched: in sync again.
            $this->followState($u, $c, $record, $to);

            return;
        }
        if ($tr === null || $from === null || $to === null) {
            $this->markOutOfSync($u, $c, $record, $from, $fc->to);

            return;
        }
        $trigger = new TriggerInfo('transition', $lc->field, $from->key, $to->key, $tr->key, $change->actorType, $change->actorId, $change->actorName, Instant::format($u->now),
            $change->eventId);
        $slaAudits = $this->prepareSlaCarry($u, $c, $record, $from, $to, $change->actor());
        $this->exitState($u, $c, $record, $from, $change->actor(), $trigger, $change->depth);
        $this->moveTo($u, $record, $to);
        $u->audit(new AuditEntry('lifecycle', 'transitioned', 'record', $change->entityId, WorkflowActor::system(), $change->entity, $change->entityId, $record->id,
            $c->definitionId, before: (object) ['state' => $from->key], after: (object) ['state' => $to->key],
            data: (object) ['transition' => $tr->key, 'label' => $tr->label, 'observed' => true], correlationId: $record->correlationId));
        foreach ($slaAudits as $a) {
            $u->audit($a);
        }
        $this->queueLifecycleEvent($u, $c, $change->entity, $change->entityId, $from->key, $to->key, $tr->key, $change->actor(), $change->depth, $record->correlationId);
        $this->startRun($u, $record, $c, "transition:{$tr->id}", "transition:{$tr->id}", $trigger, $change->actor(), $change->depth, null);
        $this->enterState($u, $c, $record, $to, $change->actor(), $trigger, $change->depth, 0);
    }

    /**
     * Archiving drops a running SLA timer; when the state's SLA can pause, its due instant is kept so restoring plans it
     * there (a clock shifted by pauses is not recounted from when the record entered the state).
     *
     * @param  list<BookmarkRecord>  $timers
     */
    private function keepRunningSla(Uow $u, InstanceRecord $record, array $timers): void
    {
        $c = $this->compiled($u, $record->definitionId, $record->version);
        $state = $c->stateById($record->state);
        if ($state?->sla === null || ! self::usesPause($state->sla)) {
            return;
        }
        $timer = array_find($timers, fn (BookmarkRecord $b) => $b->purpose === BookmarkPurposes::SLA_PREFIX.$state->id);
        if ($timer === null) {
            return;
        }
        $clocks = SlaClocks::read($record);
        if (isset($clocks[$state->id])) {
            return;
        }
        $clocks[$state->id] = new SlaClock(due: $timer->dueAt);
        SlaClocks::write($record, $clocks);
        $record->updatedAt = $u->now;
        $u->s->updateInstance($record);
    }
}
