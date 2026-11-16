<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Audit\Masker;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\SlaClocks;
use Zouriel\Workflows\Core\Engine\TriggerInfo;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\Views\RecordView;
use Zouriel\Workflows\Core\Engine\Views\ResetItem;
use Zouriel\Workflows\Core\Engine\Views\ResetOption;
use Zouriel\Workflows\Core\Engine\Views\ResetPreview;
use Zouriel\Workflows\Core\Host\ActionUndoContext;
use Zouriel\Workflows\Core\Host\UndoableWorkflowAction;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Lint\Linter;
use Zouriel\Workflows\Core\Model\LifecycleReset;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Model\Transition;
use Zouriel\Workflows\Core\Registry\FieldOption;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Storage\StepStatus;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * Starting a record's workflow over (spec §6 reset): the reset itself, the reset list of what the workflows did and
 * taking chosen items back. Port of .NET `Engine/WorkflowEngine.Reset.cs` and the reset part of `WorkflowEngine.Lifecycle.cs`.
 */
trait Resets
{
    private int $lastJournalSeq = 0;

    /**
     * An undo journal entry (spec §6) as JSON, with its `seq`: the clock in milliseconds × 1000, raised past the last one
     * handed out, so entries written in one unit of work (which share a start time) keep their order.
     */
    public function journal(stdClass $entry): string
    {
        $this->lastJournalSeq = max($this->now() * 1000, $this->lastJournalSeq + 1);
        $entry->seq = $this->lastJournalSeq;

        return Values::serialize($entry);
    }

    /**
     * A person starts a record's workflow over (spec §6 reset): one of `reset.who` moves it back to `reset.to` (the
     * initial state by default); its open runs and tasks are cancelled (and the automations about it, unless
     * `cancelAutomations` is false); the chosen items of the reset list are taken back; then the reset steps run.
     *
     * @param  list<string>  $undo  ids of reset-list items to take back
     * @param  string|null  $token  the reset list's token; `resetChanged` when the list changed since
     */
    public function reset(string $entity, string $id, ?stdClass $input, WorkflowActor $user, ?string $lifecycle = null, array $undo = [], ?string $token = null,
        ?string $comment = null): RecordView
    {
        if (! $user->isUser()) {
            throw new WorkflowException('notAllowed', 'Only a signed-in person can reset a workflow.');
        }

        return $this->unit(function (Uow $u) use ($entity, $id, $input, $user, $lifecycle, $undo, $token, $comment) {
            $c = $this->lifecycleOf($u, $entity, $id, $lifecycle);
            $record = $this->ensureRecordInstance($u, $c, $entity, $id, $user);
            if ($record->version !== $c->version) {
                $c = $this->compiled($u, $record->definitionId, $record->version);
            }
            $reset = $c->lifecycle()->reset ?? throw new WorkflowException('resetDisabled', "{$c->definition->title} cannot be reset.");
            $label = $reset->label ?? 'Reset workflow';
            if (! $this->mayReset($u, $c, $record, $user)) {
                throw new WorkflowException('notAllowed', "You are not allowed to “{$label}”.");
            }
            if (! self::resetOfferedIn($reset, $record)) {
                throw new WorkflowException('notAllowed', "“{$label}” isn't offered in “".($c->stateById($record->state)?->label ?? 'this state').'”.');
            }
            $to = self::resetTarget($c);
            if ($record->state === $to->id && $record->status !== InstanceStatus::OUT_OF_SYNC && $this->runningAbout($u, $record, $reset) === []) {
                throw new WorkflowException('nothingToReset', "The record is already in “{$to->label}” with nothing running.");
            }
            $input = $this->checkedForm($reset->input?->form, $input ?? new stdClass, $u);
            $comment = self::checkRemarks($reset->resolvedComment(), $comment, true, $label);
            $from = $c->stateById($record->state);
            $trigger = new TriggerInfo('reset', $c->lifecycle()->field, $from?->key, $to->key, actorType: $user->type, actorId: $user->id, actorName: $user->name,
                at: Instant::format($u->now), comment: $comment);
            foreach ($this->runningAbout($u, $record, $reset) as $run) {
                $this->cancelInstance($u, $run, $user, 'The workflow was reset by '.($user->name ?? $user->id).'.');
            }
            // What the person chose to take back of the workflow's work (spec §6 reset list).
            $undone = $this->undoChosen($u, $c, $record, $undo, $token, $user);
            $runExitEnter = $reset->runExitEnter === true;
            if ($from !== null && $runExitEnter) {
                $this->exitState($u, $c, $record, $from, $user, $trigger, 0);
            } else {
                foreach ($u->s->bookmarks($record->id) as $b) {
                    $u->s->removeBookmark($b->id);
                }
            }
            $this->writeState($u, $c, $entity, $id, $to, $user, 0, $record->id);
            SlaClocks::clear($record);
            $this->moveTo($u, $record, $to);
            $u->audit(new AuditEntry('lifecycle', 'reset', 'record', $id, $user, $entity, $id, $record->id, $c->definitionId, before: (object) ['state' => $from?->key],
                after: (object) ['state' => $to->key], data: self::withComment((object) ['label' => $label, 'input' => $input, 'undone' => $undone], $comment),
                correlationId: $record->correlationId));
            $u->log?->add('lifecycle', 'reset', $id, ($from->label ?? '?')." → {$to->label} ({$label})");
            $this->queueLifecycleEvent($u, $c, $entity, $id, $from?->key, $to->key, null, $user, 0, $record->correlationId, $comment);
            // The reset steps read the answers (input.*) and may change this record and related ones.
            $this->startRun($u, $record, $c, 'reset', 'reset', $trigger, $user, 0, $input);
            if ($record->state === $to->id) {
                if ($runExitEnter) {
                    $this->enterState($u, $c, $record, $to, $user, $trigger, 0, 0, $input);
                } elseif (! $to->isTerminal()) {
                    $this->planStateTimers($u, $c, $record, $to);
                }
            }

            return $this->recordView($u, $entity, $id, $user);
        });
    }

    /** The state a reset goes back to: `reset.to`, else the initial state. */
    public static function resetTarget(CompiledDefinition $c): State
    {
        $to = $c->lifecycle()->reset?->to;

        return ($to === null ? null : $c->stateById($to)) ?? $c->initialState();
    }

    /** Whether the reset is offered in the record's current state (`reset.states`; absent = every state). */
    public static function resetOfferedIn(LifecycleReset $reset, InstanceRecord $record): bool
    {
        return $reset->states === null || ($record->state !== null && in_array($record->state, $reset->states, true));
    }

    /** Whether the viewer may reset the record's workflow (it has a reset and they are in its `who`). */
    public function mayReset(Uow $u, CompiledDefinition $c, InstanceRecord $record, WorkflowActor $actor): bool
    {
        $reset = $c->lifecycle()->reset;

        return $reset !== null && $this->isAmong($u->scope($record), $reset->who, $actor, $record->initiatorId);
    }

    /** @return list<InstanceRecord> what a reset stops: the record's unfinished runs, and the automations about it unless the reset keeps them */
    public function runningAbout(Uow $u, InstanceRecord $record, LifecycleReset $reset): array
    {
        $running = [];
        foreach ($u->s->queryInstances(new InstanceQuery(kinds: [InstanceKinds::RUN], parentId: $record->id, take: 1000)) as $run) {
            if (! InstanceStatus::isFinished($run->status) && ! isset($u->driving[$run->id])) {
                $running[] = $run;
            }
        }
        if ($reset->cancelAutomations !== false) {
            foreach ($u->s->queryInstances(new InstanceQuery(entity: $record->entity, entityId: $record->entityId, kinds: [InstanceKinds::AUTOMATION], take: 1000)) as $a) {
                if (! InstanceStatus::isFinished($a->status) && ! isset($u->driving[$a->id])) {
                    $running[] = $a;
                }
            }
        }

        return $running;
    }

    /**
     * What resetting a record would do (spec §6 reset): the reset option and every change its workflows made since it
     * last started over. Only for people that lifecycle's reset allows (`notAllowed`).
     */
    public function getResetPreview(string $entity, string $id, WorkflowActor $viewer, ?string $lifecycle = null): ResetPreview
    {
        return $this->unit(function (Uow $u) use ($entity, $id, $viewer, $lifecycle) {
            if ($this->registry->entity($entity) === null) {
                throw new WorkflowException('unknownEntity', "'{$entity}' is not a workflow record type.");
            }
            $snapshot = $u->s->entities()->get($entity, $id) ?? throw new WorkflowException('noRecord', 'That record does not exist.');
            $c = $this->lifecycleOf($u, $entity, $id, $lifecycle);
            $record = self::findRecordInstance($u, $c->definitionId, $entity, $id);
            $shown = $record ?? self::transientRecordInstance($c, $entity, $id, $snapshot, $u->now);
            $rc = $shown->version === $c->version ? $c : $this->compiled($u, $shown->definitionId, $shown->version);
            $option = $this->offeredReset($u, $rc, $shown, $viewer) ?? throw new WorkflowException('notAllowed', "You cannot reset this record's workflow now.");
            [$items, , $token] = $record === null ? [[], [], self::tokenOf([])] : $this->planUndo($u, $rc, $record);

            return new ResetPreview($option, array_map(fn (array $p) => $p['item'], $items), $token);
        });
    }

    /** The reset a lifecycle offers the viewer for a record: null when not in its states, not among its `who`, or it would change nothing. */
    public function offeredReset(Uow $u, CompiledDefinition $rc, InstanceRecord $record, WorkflowActor $viewer): ?ResetOption
    {
        $r = $rc->lifecycle()->reset;
        if ($r === null || ! self::resetOfferedIn($r, $record) || ! $this->mayReset($u, $rc, $record, $viewer)) {
            return null;
        }
        $back = self::resetTarget($rc);
        // Not offered when it would change nothing (already back, nothing running).
        $changes = $record->state !== $back->id || $record->status === InstanceStatus::OUT_OF_SYNC || $this->runningAbout($u, $record, $r) !== [];

        return $changes ? new ResetOption($r->label ?? 'Reset workflow', $back->key, $back->label, $r->confirm, $r->input?->form === null ? null : Json::copy($r->input->form),
            $r->resolvedComment()) : null;
    }

    /**
     * The journal entries a reset of this record instance considers (spec §6): those of its own runs (not reset runs)
     * and of the automations about the record, not yet settled by an earlier reset, in the order they happened.
     *
     * @return list<array{step: StepRecord, run: InstanceRecord, undo: stdClass, seq: int}>
     */
    private static function journalEntries(Uow $u, InstanceRecord $record): array
    {
        $runs = [
            ...$u->s->queryInstances(new InstanceQuery(kinds: [InstanceKinds::RUN], parentId: $record->id, take: 1000)),
            ...$u->s->queryInstances(new InstanceQuery(entity: $record->entity, entityId: $record->entityId, kinds: [InstanceKinds::AUTOMATION], take: 1000)),
        ];
        $entries = [];
        $seen = [];
        foreach ($runs as $run) {
            if ($run->reason === 'reset' || isset($seen[$run->id])) {
                continue;
            }
            $seen[$run->id] = true;
            foreach ($u->s->steps($run->id) as $s) {
                $undo = $s->undo === null ? null : Values::fromJson($s->undo);
                if ($s->status === StepStatus::COMPLETED && $undo instanceof stdClass && ! property_exists($undo, 'settled')) {
                    $entries[] = ['step' => $s, 'run' => $run, 'undo' => $undo, 'seq' => is_float($undo->seq ?? null) ? (int) $undo->seq : $s->startedAt * 1000];
                }
            }
        }
        // A stable sort: entries keep their order even when steps share a start time.
        usort($entries, fn ($a, $b) => $a['seq'] <=> $b['seq']);

        return $entries;
    }

    /**
     * What the record's workflows did since it last started over, merged per field and checked against now.
     *
     * @return array{0: list<array{item: ResetItem, entity: string, id: string, field?: string, before?: mixed, stepId?: string, undo?: stdClass, handler?: ?UndoableWorkflowAction}>, 1: list<array{step: StepRecord, undo: stdClass}>, 2: string}
     */
    private function planUndo(Uow $u, CompiledDefinition $c, InstanceRecord $record): array
    {
        $entity = $record->entity ?? '';
        $id = $record->entityId ?? '';
        $done = self::journalEntries($u, $record);
        $kind = fn (array $d) => is_string($d['undo']->kind ?? null) ? $d['undo']->kind : null;
        $entityOf = fn (array $d) => is_string($d['undo']->entity ?? null) ? $d['undo']->entity : '';
        $idOf = fn (array $d) => Values::text($d['undo']->id ?? null);
        $names = function (array $d) use ($u) {
            $rc = $this->compiled($u, $d['run']->definitionId, $d['run']->version);

            return [$rc->definition->title, $rc->step($d['step']->nodeId)?->title ?? Linter::STEP_LABELS[$d['step']->stepType] ?? $d['step']->stepType];
        };
        // Hidden values (spec §11): sensitive registry fields and the settings' masked fields, of any record listed.
        $sensitive = fn (string $e, string $field) => ($fd = $this->registry->field($e, $field)) !== null && Masker::isSensitive($fd, $e, $u->settings);
        $recordName = function (string $e, string $rid) use ($u, $sensitive) {
            $descriptor = $this->registry->entity($e);
            $snapshot = $u->s->entities()->get($e, $rid);
            // A title field whose values are hidden names the record by its id instead.
            $title = $descriptor?->titleField !== null && $snapshot !== null && ! $sensitive($e, $descriptor->titleField) ? Values::text($snapshot->get($descriptor->titleField)) : null;

            return ($descriptor->label ?? $e).' “'.($title === null || $title === '' ? $rid : $title).'”';
        };
        // Values as people read them: option labels, people's names, related records' titles.
        $show = function (string $e, string $field, mixed $v) use ($sensitive, $recordName) {
            $f = $this->registry->field($e, $field);

            return match (true) {
                $sensitive($e, $field) => '(hidden)',
                Values::isEmpty($v) => 'nothing',
                $f?->type === FieldTypes::USER => $this->options->identity->user(Values::text($v))?->name ?? Values::text($v),
                $f?->type === FieldTypes::RELATION && $f->entity !== null => $recordName($f->entity, Values::text($v)),
                default => $this->optionText($e, $field, $v),
            };
        };

        $plan = [];
        // Per item, what the token covers beyond the item itself: the raw values it compares (never a hidden value).
        $prints = [];
        $created = [];
        foreach ($done as $d) {
            if ($kind($d) === 'created') {
                $created[$entityOf($d)."\n".$idOf($d)] = true;
            }
        }
        // Records the workflows created: deleting them takes back their creation and everything set on them since.
        foreach ($done as $d) {
            if ($kind($d) !== 'created') {
                continue;
            }
            [$e, $rid] = [$entityOf($d), $idOf($d)];
            $snapshot = $u->s->entities()->get($e, $rid);
            if ($snapshot === null) {
                continue;
            }
            // What the record should hold if only the workflows touched it: its first values, then their later writes.
            $expected = ($d['undo']->values ?? null) instanceof stdClass ? Json::copy($d['undo']->values) : null;
            if ($expected !== null) {
                foreach ($done as $w) {
                    if ($kind($w) === 'fields' && $entityOf($w) === $e && $idOf($w) === $rid && $w['seq'] > $d['seq']) {
                        foreach (Values::members($w['undo']->after ?? null) as $k => $v) {
                            $expected->{$k} = $v;
                        }
                    }
                }
            }
            $changedSince = $expected !== null && array_any(array_keys(Values::members($expected)), fn ($k) => $snapshot->has($k)
                && ! Values::deepEquals($snapshot->get($k), $expected->{$k}));
            [$wf, $step] = $names($d);
            $plan[] = ['item' => new ResetItem("created:{$e}:{$rid}", 'created', 'Delete '.$recordName($e, $rid), 'Created by the workflow', $wf, $step, true, ! $changedSince,
                $changedSince ? 'Changed since the workflow created it; ticking it deletes it anyway.' : null), 'entity' => $e, 'id' => $rid];
            $readable = Values::members($snapshot->values);
            ksort($readable, SORT_STRING);
            $prints[] = (object) ['changed' => $changedSince, 'values' => Values::object(array_filter($readable, fn ($k) => ! $sensitive($e, (string) $k), ARRAY_FILTER_USE_KEY))];
        }

        // Fields: one item per field of each record, back to its value before the first change.
        $groups = [];
        foreach ($done as $d) {
            if ($kind($d) !== 'fields') {
                continue;
            }
            foreach (array_keys(Values::members($d['undo']->after ?? null)) as $field) {
                $groups[$entityOf($d)."\n".$idOf($d)."\n".$field][] = $d;
            }
        }
        foreach ($groups as $key => $group) {
            [$e, $rid, $field] = explode("\n", (string) $key, 3);
            // The lifecycle's own field is moved by the reset itself; a record about to be deleted needs nothing.
            if (($e === $entity && $rid === $id && $field === $c->lifecycle()->field) || isset($created["{$e}\n{$rid}"])) {
                continue;
            }
            $snapshot = $u->s->entities()->get($e, $rid);
            if ($snapshot === null) {
                continue;
            }
            $first = $group[0];
            $last = $group[count($group) - 1];
            $before = Values::normalize($first['undo']->before->{$field} ?? null);
            $after = Values::normalize($last['undo']->after->{$field} ?? null);
            $now = Values::normalize($snapshot->get($field));
            if (Values::deepEquals($now, $before)) {
                continue;
            }
            $changedSince = ! Values::deepEquals($now, $after);
            [$wf, $step] = $names($last);
            $label = $this->registry->field($e, $field)->label ?? $field;
            $plan[] = ['item' => new ResetItem("field:{$e}:{$rid}:{$field}", 'field', "{$label} of ".$recordName($e, $rid).': back to '.$show($e, $field, $before),
                'Now '.$show($e, $field, $now), $wf, $step, true, ! $changedSince,
                $changedSince ? 'Changed again since the workflow set it to '.$show($e, $field, $after).'; ticking it overwrites that.' : null),
                'entity' => $e, 'id' => $rid, 'field' => $field, 'before' => $before];
            // A hidden value only says what the item already shows.
            $prints[] = $sensitive($e, $field) ? (object) ['changed' => $changedSince] : (object) ['now' => $now];
        }

        // Actions, messages, webhooks and moves of other records, in the order they happened.
        foreach ($done as $d) {
            $k = $kind($d);
            if (! in_array($k, ['action', 'message', 'webhook', 'move'], true)) {
                continue;
            }
            [$wf, $step] = $names($d);
            $undo = $d['undo'];
            if ($k === 'action') {
                $actionKey = is_string($undo->action ?? null) ? $undo->action : '';
                $descriptor = $this->registry->action($actionKey);
                $handler = $descriptor === null || $u->simulation ? null : $this->options->actions->resolve($descriptor);
                $handler = $handler instanceof UndoableWorkflowAction ? $handler : null;
                $plan[] = ['item' => new ResetItem("action:{$d['step']->id}", 'action', 'Undo “'.($descriptor->label ?? $actionKey).'”', null, $wf, $step, $handler !== null,
                    $handler !== null, $handler === null ? 'The app has no way to undo this action.' : null), 'entity' => $entity, 'id' => $id, 'stepId' => $d['step']->id,
                    'undo' => $undo, 'handler' => $handler];
            } elseif ($k === 'move') {
                [$e, $rid] = [$entityOf($d), $idOf($d)];
                $lifecycleKey = is_string($undo->lifecycle ?? null) ? $undo->lifecycle : '';
                // This record's own lifecycle is moved by the reset itself.
                if ($e === $entity && $rid === $id && $lifecycleKey === $c->definition->key) {
                    continue;
                }
                $transitionKey = is_string($undo->transition ?? null) ? $undo->transition : '';
                $label = $transitionKey;
                $definition = $u->s->definitionByKey($lifecycleKey);
                if ($definition !== null && $definition->version > 0) {
                    $label = array_find($this->compiled($u, $definition->id, $definition->version)->definition->lifecycle->transitions ?? [],
                        fn (Transition $t) => $t->key === $transitionKey)?->label ?? $transitionKey;
                }
                $plan[] = ['item' => new ResetItem("move:{$d['step']->id}", 'move', "“{$label}” on ".$recordName($e, $rid), null, $wf, $step, false, false,
                    "Moves of other records aren't taken back."), 'entity' => $e, 'id' => $rid];
            } else {
                $title = $k === 'message'
                    ? 'Message'.(is_string($undo->subject ?? null) && $undo->subject !== '' ? " “{$undo->subject}”" : '').' to '.implode(', ', array_map(Values::text(...), is_array($undo->to ?? null) ? $undo->to : []))
                    : 'Webhook '.Values::text($undo->method ?? null).' '.Values::text($undo->url ?? null);
                $plan[] = ['item' => new ResetItem("{$k}:{$d['step']->id}", $k, $title, null, $wf, $step, false, false,
                    $k === 'message' ? "Already sent; it can't be taken back." : "Already called; it can't be taken back."), 'entity' => $entity, 'id' => $id];
            }
            $prints[] = null;
        }
        // Titles and details quote values (message subjects, related records' names): hide the sensitive ones of the record
        // reset and of the record each item is about.
        $mine = $u->masker($entity, $id);
        foreach ($plan as $i => $p) {
            $theirs = $p['entity'] === $entity && $p['id'] === $id ? $mine : $u->masker($p['entity'], $p['id']);
            $hide = fn (?string $text) => $theirs->maskText($mine->maskText($text));
            $plan[$i]['item'] = $p['item']->with($hide($p['item']->title), $hide($p['item']->detail), $hide($p['item']->warning));
        }
        $token = self::tokenOf(array_map(fn (array $p, int $i) => (object) [
            'item' => Json::encode((object) ['id' => $p['item']->id, 'title' => $p['item']->title, 'detail' => $p['item']->detail, 'undoable' => $p['item']->undoable,
                'selected' => $p['item']->selected, 'warning' => $p['item']->warning]),
            'values' => $prints[$i],
        ], $plan, array_keys($plan)));

        return [$plan, array_map(fn (array $d) => ['step' => $d['step'], 'undo' => $d['undo']], $done), $token];
    }

    /** The reset list's token: a hash of its items and the values they compare. @param  list<mixed>  $items */
    private static function tokenOf(array $items): string
    {
        return substr(hash('sha256', Values::serialize($items)), 0, 32);
    }

    /** A stored value as people read it (an option's label for enums). */
    private function optionText(string $entity, string $field, mixed $value): string
    {
        $text = Values::text($value);

        return array_find($this->registry->field($entity, $field)->options ?? [], fn (FieldOption $o) => Values::text($o->value) === $text)?->label ?? $text;
    }

    /**
     * Takes back the chosen items of a reset (spec §6), inside the reset's unit of work; returns their titles. Every
     * journal entry the list was built from is settled, so the next reset only lists what happens after this one.
     *
     * @param  list<string>  $chosen
     * @return list<string>
     */
    private function undoChosen(Uow $u, CompiledDefinition $c, InstanceRecord $record, array $chosen, ?string $token, WorkflowActor $user): array
    {
        [$plan, $considered, $planToken] = $this->planUndo($u, $c, $record);
        $changed = new WorkflowException('resetChanged', 'What the workflow did has changed since this list was opened. Open it again.');
        if ($token !== null && $token !== $planToken) {
            throw $changed;
        }
        $byId = [];
        foreach ($plan as $p) {
            $byId[$p['item']->id] = $p;
        }
        foreach ($chosen as $idChosen) {
            $p = $byId[$idChosen] ?? throw $changed;
            if (! $p['item']->undoable) {
                throw new WorkflowException('notUndoable', "“{$p['item']->title}” can't be undone.");
            }
        }
        // Settled, ticked or not: unticked items stay as they are and are not offered again.
        $settledBy = self::newId();
        foreach ($considered as ['step' => $step, 'undo' => $undo]) {
            $undo->settled = $settledBy;
            $step->undo = Values::serialize($undo);
            $u->s->updateStep($step);
        }
        $picked = array_values(array_filter($plan, fn (array $p) => in_array($p['item']->id, $chosen, true)));
        if ($picked === []) {
            return [];
        }
        // Taking things back starts no automations, so none can redo what was undone.
        $this->scopes->run($user, 1, $record->id, $c->definitionId, $record->correlationId, function () use ($picked, $record, $user, $u) {
            // App actions first (newest first), then fields back to their old values, then created records deleted.
            foreach (array_reverse(array_filter($picked, fn ($p) => $p['item']->kind === 'action')) as $p) {
                $undo = $p['undo'];
                $p['handler']->undo(new ActionUndoContext(is_string($undo->action ?? null) ? $undo->action : '',
                    ($undo->input ?? null) instanceof stdClass ? $undo->input : new stdClass, ($undo->output ?? null) instanceof stdClass ? $undo->output : new stdClass,
                    $record->entity, $record->entityId, $user, $p['stepId']));
            }
            $fields = [];
            foreach ($picked as $p) {
                if ($p['item']->kind === 'field') {
                    $fields[$p['entity']."\n".$p['id']][$p['field']] = $p['before'];
                }
            }
            foreach ($fields as $key => $values) {
                [$e, $rid] = explode("\n", (string) $key, 2);
                $u->s->entities()->setFields($e, $rid, Values::object($values));
            }
            foreach ($picked as $p) {
                if ($p['item']->kind === 'created') {
                    $u->s->entities()->delete($p['entity'], $p['id']);
                }
            }
        }, suppressTriggers: true);

        return array_map(fn (array $p) => $p['item']->title, $picked);
    }
}
