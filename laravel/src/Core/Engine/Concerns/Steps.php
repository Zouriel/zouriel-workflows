<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use JsonException;
use stdClass;
use Throwable;
use Zouriel\Forms\Json;
use Zouriel\Forms\Values as FormValues;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Engine\Exec;
use Zouriel\Workflows\Core\Engine\Frame;
use Zouriel\Workflows\Core\Engine\JoinState;
use Zouriel\Workflows\Core\Engine\RunState;
use Zouriel\Workflows\Core\Engine\Token;
use Zouriel\Workflows\Core\Engine\TriggerInfo;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\ValueScope;
use Zouriel\Workflows\Core\Expressions\WorkflowConditions;
use Zouriel\Workflows\Core\Host\ActionContext;
use Zouriel\Workflows\Core\Host\NotificationRecipient;
use Zouriel\Workflows\Core\Host\WebhookFailedException;
use Zouriel\Workflows\Core\Host\WebhookRequest;
use Zouriel\Workflows\Core\Host\WebhookResponse;
use Zouriel\Workflows\Core\Host\WorkflowAction;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Host\WorkflowNotification;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\ActionStep;
use Zouriel\Workflows\Core\Model\ApplyTransitionStep;
use Zouriel\Workflows\Core\Model\Assignee;
use Zouriel\Workflows\Core\Model\BranchStep;
use Zouriel\Workflows\Core\Model\CreateRecordStep;
use Zouriel\Workflows\Core\Model\EndStep;
use Zouriel\Workflows\Core\Model\ForEachStep;
use Zouriel\Workflows\Core\Model\GoToStep;
use Zouriel\Workflows\Core\Model\HumanStep;
use Zouriel\Workflows\Core\Model\NotifyStep;
use Zouriel\Workflows\Core\Model\ParallelStep;
use Zouriel\Workflows\Core\Model\SetFieldsStep;
use Zouriel\Workflows\Core\Model\SetVariablesStep;
use Zouriel\Workflows\Core\Model\Step;
use Zouriel\Workflows\Core\Model\SubWorkflowStep;
use Zouriel\Workflows\Core\Model\SwitchStep;
use Zouriel\Workflows\Core\Model\WaitStep;
use Zouriel\Workflows\Core\Model\WebhookStep;
use Zouriel\Workflows\Core\Registry\FieldDescriptor;
use Zouriel\Workflows\Core\Registry\FieldOption;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Storage\BookmarkKinds;
use Zouriel\Workflows\Core\Storage\BookmarkPurposes;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\DefinitionStatus;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Storage\StepStatus;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;
use Zouriel\Workflows\Core\Support\AbsoluteUrl;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\Time\WorkCalendar;
use Zouriel\Workflows\Core\WorkflowException;

/** Every step type (spec §7) and step errors (retry, onError, incidents). Port of .NET `Engine/WorkflowEngine.Steps.cs`. */
trait Steps
{
    /** Errors that retrying cannot fix: the step fails at once (onError or an incident). */
    private const array NOT_RETRYABLE = ['fieldNotSettable', 'unknownAction', 'missingInput', 'noRecord', 'goToInvalidTarget', 'noAssignees', 'webhookOriginNotAllowed',
        'fieldOwned', 'subWorkflowDepth', 'badHeader', 'unknownValue', 'badMethod'];

    private function executeStep(Exec $x, Token $t, Step $step): void
    {
        try {
            match (true) {
                $step instanceof BranchStep => $this->branch($x, $t, $step),
                $step instanceof SwitchStep => $this->switchStep($x, $t, $step),
                $step instanceof ParallelStep => $this->parallel($x, $t, $step),
                $step instanceof ForEachStep => $this->forEach($x, $t, $step),
                $step instanceof HumanStep => $this->createTask($x, $t, $step),
                $step instanceof WaitStep => $this->waitStep($x, $t, $step),
                $step instanceof SetFieldsStep => $this->setFields($x, $t, $step),
                $step instanceof CreateRecordStep => $this->createRecord($x, $t, $step),
                $step instanceof SetVariablesStep => $this->setVariables($x, $t, $step),
                $step instanceof ApplyTransitionStep => $this->applyTransitionStep($x, $t, $step),
                $step instanceof NotifyStep => $this->notify($x, $t, $step),
                $step instanceof WebhookStep => $this->webhook($x, $t, $step),
                $step instanceof ActionStep => $this->action($x, $t, $step),
                $step instanceof SubWorkflowStep => $this->subWorkflow($x, $t, $step),
                $step instanceof GoToStep => $this->goTo($x, $t, $step),
                $step instanceof EndStep => $this->end($x, $t, $step),
                default => throw new WorkflowException('unknownStepType', "This engine cannot run '{$step->type}' steps."),
            };
        } catch (WorkflowConcurrencyException $e) {
            throw $e;
        } catch (Throwable $e) {
            $this->handleStepError($x, $t, $step, $e, null);
        }
    }

    // ── Flow ──────────────────────────────────────────────────────────────

    private function branch(Exec $x, Token $t, BranchStep $b): void
    {
        $record = $this->beginStep($x, $t, $b, null);
        $scope = $x->scope($t);
        [$taken, $list] = [null, null];
        foreach ($b->paths as $p) {
            if ($scope->condition($p->when)) {
                [$taken, $list] = [$p->id, "{$b->id}/path:{$p->id}"];
                break;
            }
        }
        if ($taken === null && $b->otherwise !== null) {
            [$taken, $list] = [$b->otherwise->id, "{$b->id}/otherwise"];
        }
        $x->s->setOutput($b->id, 'path', $taken);
        $x->u->audit(self::stepAudit($x, $b, 'branchTaken', (object) ['path' => $taken]));
        $this->completeStep($x, $record, $b, $x->s->output($b->id));
        if ($list === null) {
            self::advance($t);
        } else {
            $t->frames[] = new Frame($list);
        }
    }

    private function switchStep(Exec $x, Token $t, SwitchStep $sw): void
    {
        $record = $this->beginStep($x, $t, $sw, null);
        $scope = $x->scope($t);
        $value = FormValues::optionValueOf($scope->resolve($sw->on));
        $match = array_find($sw->cases, fn ($c) => WorkflowConditions::evaluate((object) ['ref' => '$v', 'op' => 'anyOf', 'value' => (object) ['const' => Json::normalize($c->values)]],
            fn () => $value, $scope->today));
        $taken = $match?->id ?? $sw->otherwise?->id;
        $x->s->setOutput($sw->id, 'case', $taken);
        $x->u->audit(self::stepAudit($x, $sw, 'branchTaken', (object) ['path' => $taken]));
        $this->completeStep($x, $record, $sw, $x->s->output($sw->id));
        if ($match !== null) {
            $t->frames[] = new Frame("{$sw->id}/case:{$match->id}");
        } elseif ($sw->otherwise !== null) {
            $t->frames[] = new Frame("{$sw->id}/otherwise");
        } else {
            self::advance($t);
        }
    }

    private function parallel(Exec $x, Token $t, ParallelStep $p): void
    {
        $record = $this->beginStep($x, $t, $p, null);
        $key = "{$t->id}:{$p->id}";
        [$item, $itemIndex] = $t->currentItem();
        $join = new JoinState($p->id, $t->id, 'parallel', required: match ($p->join->type) {
            'any' => 1,
            'count' => max(1, min($p->join->count ?? 1, count($p->paths))),
            default => count($p->paths),
        });
        foreach ($p->paths as $path) {
            $child = new Token($x->s->newTokenId(), [new Frame("{$p->id}/path:{$path->id}", item: $item, itemIndex: $itemIndex)], $t->id, $key, $path->id);
            $join->children[] = $child->id;
            $x->s->tokens[] = $child;
        }
        $x->s->joins[$key] = $join;
        self::wait($t, 'join', $record);
    }

    private function forEach(Exec $x, Token $t, ForEachStep $fe): void
    {
        $record = $this->beginStep($x, $t, $fe, null);
        $scope = $x->scope($t);
        [$owner, $ownerId, $field] = $this->collectionOwner($scope, $fe->collection);
        $items = [];
        if ($owner !== null && $ownerId !== null && $field !== null) {
            $max = min($fe->max, $x->u->settings->forEachMax());
            $related = $this->registry->field($owner, $field)?->entity;
            foreach ($x->u->s->entities()->collection($owner, $ownerId, $field) as $snapshot) {
                $item = Json::copy($snapshot->values);
                $item->id = $snapshot->id;
                $item->{'$entity'} = $related;
                if ($fe->when !== null) {
                    $itemScope = $x->u->scope($x->i, $x->s, new Token('item', [new Frame(item: $item, itemIndex: count($items))]));
                    if (! $itemScope->condition($fe->when)) {
                        continue;
                    }
                }
                $items[] = $item;
                if (count($items) >= $max) {
                    break;
                }
            }
        }
        if ($items === []) {
            $x->s->setOutput($fe->id, 'count', 0);
            $this->completeStep($x, $record, $fe, $x->s->output($fe->id));
            self::advance($t);

            return;
        }
        if (! $fe->isParallel()) {
            $x->s->loops["{$t->id}:{$fe->id}"] = $items;
            $t->stepRecord = $record->id;
            $record->status = StepStatus::WAITING;
            $x->u->s->updateStep($record);
            $t->frames[] = new Frame("{$fe->id}/each", item: $items[0], itemIndex: 0);

            return;
        }
        $join = new JoinState($fe->id, $t->id, 'forEach', items: $items, pending: range(0, count($items) - 1), concurrency: max(1, min(20, $fe->concurrency ?? 5)));
        $x->s->joins["{$t->id}:{$fe->id}"] = $join;
        while ($join->pending !== [] && count($join->children) < $join->concurrency) {
            self::spawnItem($x, $t, $fe, $join);
        }
        self::wait($t, 'join', $record);
    }

    /** @return array{0: ?string, 1: ?string, 2: ?string} for a collection path like `entity.documents`: the record that owns it, and the field */
    private function collectionOwner(ValueScope $scope, string $path): array
    {
        $parts = explode('.', $path);
        $entity = $parts[0] === 'item' ? $scope->itemEntity : $scope->entity;
        $id = $parts[0] === 'item' ? $scope->itemId() : $scope->entityId;
        for ($i = 1; $i < count($parts) - 1 && $entity !== null && $id !== null; $i++) {
            $f = $this->registry->field($entity, $parts[$i]);
            if ($f?->type !== FieldTypes::RELATION) {
                return [null, null, null];
            }
            $related = $scope->load($entity, $id)?->get($f->key);
            $id = $related === null ? null : Values::text($related);
            $entity = $f->entity;
        }

        return [$entity, $id, count($parts) > 1 ? $parts[count($parts) - 1] : null];
    }

    private function goTo(Exec $x, Token $t, GoToStep $g): void
    {
        // Counted per item of every enclosing sequential forEach (each item gets its own maxTimes, spec §7).
        $counter = "{$t->id}:{$g->id}".implode('', array_map(fn (Frame $f) => ":{$f->list}#{$f->itemIndex}", array_filter($t->frames, fn (Frame $f) => $f->itemIndex !== null)));
        $count = $x->s->goTo[$counter] ?? 0;
        if ($count >= $g->maxTimes) {
            $x->u->audit(self::stepAudit($x, $g, 'skipped', (object) ['reason' => 'maxTimes', 'count' => $count]));
            self::advance($t);

            return;
        }
        $location = $x->c->steps[$g->target] ?? throw new WorkflowException('goToInvalidTarget', 'The step to go back to no longer exists.');
        $depth = -1;
        foreach ($t->frames as $i => $f) {
            if ($f->list === $location->listId) {
                $depth = $i;
            }
        }
        if ($depth < 0) {
            throw new WorkflowException('goToInvalidTarget', 'Go back can only return to an earlier step on the same path.');
        }
        $x->s->goTo[$counter] = $count + 1;
        $x->s->setOutput($g->id, 'count', $count + 1);
        $x->u->audit(self::stepAudit($x, $g, 'completed', (object) ['target' => $g->target, 'count' => $count + 1]));
        // Leaving nested lists drops their loop state.
        $t->frames = array_slice($t->frames, 0, $depth + 1);
        foreach (array_keys($x->s->loops) as $key) {
            if (! str_starts_with($key, "{$t->id}:")) {
                continue;
            }
            $l = $x->c->steps[substr($key, strlen($t->id) + 1)] ?? null;
            if ($l !== null && $l->listId === $location->listId && $l->index >= $location->index) {
                unset($x->s->loops[$key]);
            }
        }
        $t->top()->index = $location->index;
        $t->top()->attempt = 1;
    }

    private function end(Exec $x, Token $t, EndStep $e): void
    {
        $reason = $e->reason === null ? null : $this->render($x->scope($t), $e->reason);
        $x->u->audit(self::stepAudit($x, $e, 'completed', (object) ['result' => $e->result, 'reason' => $reason]));
        $this->finish($x, $e->result === 'failed' ? InstanceStatus::FAILED : InstanceStatus::COMPLETED, $reason);
    }

    // ── Waiting ───────────────────────────────────────────────────────────

    private function waitStep(Exec $x, Token $t, WaitStep $w): void
    {
        $record = $this->beginStep($x, $t, $w, null);
        $until = $w->until;
        $scope = $x->scope($t);
        switch ($until->type) {
            case 'duration':
                $this->addTimer($x, $t, $w, BookmarkPurposes::WAIT, $this->due($x, $until->duration ?? '', $until->calendar), null);
                self::wait($t, 'timer', $record);
                break;
            case 'date':
                $due = self::dateWaitDue($x->u->calendar, $scope->operand($until->date), $until->offset, $until->at);
                if ($due === null || $due <= $x->u->now) {
                    $x->s->setOutput($w->id, 'timedOut', false);
                    $this->completeStep($x, $record, $w, $x->s->output($w->id));
                    self::advance($t);

                    return;
                }
                // A date beyond the calendar (year 9999) never arrives: the step waits without a timer.
                if ($due !== Instant::MAX) {
                    $this->addTimer($x, $t, $w, BookmarkPurposes::WAIT, $due, null);
                }
                self::wait($t, 'timer', $record);
                break;
            case 'condition':
                if ($scope->condition($until->when)) {
                    $x->s->setOutput($w->id, 'timedOut', false);
                    $this->completeStep($x, $record, $w, $x->s->output($w->id));
                    self::advance($t);

                    return;
                }
                $x->u->s->addBookmark($this->waitBookmark($x, $t, $w, BookmarkKinds::CONDITION, BookmarkPurposes::CONDITION));
                $this->addTimer($x, $t, $w, BookmarkPurposes::TIMEOUT, $this->due($x, $until->timeout ?? '', null), null);
                self::wait($t, 'condition', $record);
                break;
            case 'signal':
                $bookmark = $this->waitBookmark($x, $t, $w, BookmarkKinds::SIGNAL, BookmarkPurposes::SIGNAL);
                $bookmark->signal = $until->signal;
                $x->u->s->addBookmark($bookmark);
                if ($until->timeout !== null) {
                    $this->addTimer($x, $t, $w, BookmarkPurposes::TIMEOUT, $this->due($x, $until->timeout, null), null);
                }
                self::wait($t, 'signal', $record);
                break;
            default:
                throw new WorkflowException('badWait', "Unknown wait type '{$until->type}'.");
        }
        $x->u->s->updateStep($record);
        $x->u->audit(self::stepAudit($x, $w, 'waiting', (object) ['until' => $until->type]));
    }

    private function waitBookmark(Exec $x, Token $t, WaitStep $w, string $kind, string $purpose): BookmarkRecord
    {
        $b = new BookmarkRecord;
        $b->id = self::newId();
        $b->instanceId = $x->i->id;
        $b->tokenId = $t->id;
        $b->nodeId = $w->id;
        $b->kind = $kind;
        $b->purpose = $purpose;
        $b->entity = $x->i->entity;
        $b->entityId = $x->i->entityId;
        $b->definitionId = $x->i->definitionId;
        $b->createdAt = $x->u->now;

        return $b;
    }

    /**
     * When a date or date-time value plus an offset arrives (dates land at `$at`, default 09:00, in the settings time
     * zone). Null when there is no date; `Instant::MAX` when it lies beyond the calendar's range (it never arrives).
     */
    public static function dateWaitDue(WorkCalendar $calendar, mixed $value, ?string $offset, ?string $at): ?int
    {
        if (! is_string($value) || strlen($value) < 10) {
            return null;
        }
        if (strlen($value) === 10) {
            if (preg_match('/^(\d{4})-(\d{2})-(\d{2})$/', $value, $m) !== 1 || ! checkdate((int) $m[2], (int) $m[3], (int) $m[1])) {
                return null;
            }
            $minutes = $at !== null && preg_match('/^([01]\d|2[0-3]):([0-5]\d)$/', $at, $tm) === 1 ? (int) $tm[1] * 60 + (int) $tm[2] : 9 * 60;
            $base = $calendar->atLocal($value, $minutes);
        } else {
            $base = Instant::parse($value);
            if ($base === null) {
                return null;
            }
        }

        return $offset === null ? $base : $calendar->add($base, IsoDuration::parse($offset));
    }

    // ── Data ──────────────────────────────────────────────────────────────

    private function setFields(Exec $x, Token $t, SetFieldsStep $sf): void
    {
        $scope = $x->scope($t);
        [$entity, $id] = $this->target($scope, $sf->target);
        if ($entity === null || $id === null) {
            throw new WorkflowException('noRecord', 'There is no record to update.');
        }
        $values = new stdClass;
        foreach ($sf->values as $v) {
            $field = $this->registry->field($entity, $v->field);
            if ($field === null || ! $field->isSettable()) {
                throw new WorkflowException('fieldNotSettable', "Workflows cannot change '{$v->field}'.");
            }
            $values->{$v->field} = self::checkOption($field, $scope->operand($v->value));
        }
        $masked = $x->u->masker($entity, $id)->mask($values);
        $record = $this->beginStep($x, $t, $sf, (object) ['entity' => $entity, 'id' => $id, 'values' => $masked]);
        // The values before the write, so a reset can restore them.
        $before = $x->u->s->entities()->get($entity, $id);
        $this->scopes->run($x->actor(), $x->i->depth + 1, $x->i->id, $x->i->definitionId, $x->i->correlationId,
            fn () => $x->u->s->entities()->setFields($entity, $id, $values));
        $x->u->forgetMasker($entity, $id);
        // What the gateway holds after the write (it may store a value differently from the operand), so the reset list
        // does not mistake its own formatting for a later change.
        $written = $x->u->s->entities()->get($entity, $id);
        $beforeValues = new stdClass;
        $afterValues = new stdClass;
        foreach (get_object_vars($values) as $key => $value) {
            $beforeValues->{$key} = $before?->get($key);
            $afterValues->{$key} = Values::normalize($written === null ? $value : $written->get($key));
        }
        $record->undo = $this->journal((object) ['kind' => 'fields', 'entity' => $entity, 'id' => $id, 'before' => $beforeValues, 'after' => $afterValues]);
        $this->completeStep($x, $record, $sf, null);
        self::advance($t);
    }

    /**
     * A value for an enum field must be one of its options (lint only sees constants; a value read from a token is
     * checked here): `unknownValue` otherwise. Empty values clear the field.
     */
    private static function checkOption(FieldDescriptor $field, mixed $value): mixed
    {
        if ($field->options === null || $field->options === [] || Values::isEmpty($value)) {
            return $value;
        }
        $text = Values::text(FormValues::optionValueOf($value));
        if (! array_any($field->options, fn (FieldOption $o) => Values::text($o->value) === $text)) {
            throw new WorkflowException('unknownValue', "'{$text}' is not one of {$field->label}'s values.");
        }

        return $value;
    }

    /** @return array{0: ?string, 1: ?string} the record a step targets: `self` (default), `item` inside forEach, or a relation path */
    public function target(ValueScope $scope, ?string $target): array
    {
        if ($target === null || $target === 'self') {
            return [$scope->entity, $scope->entityId];
        }
        if ($target === 'item') {
            return [$scope->itemEntity, $scope->itemId()];
        }
        [$entity, $id, $field] = $this->collectionOwner($scope, $target);
        if ($entity === null || $id === null || $field === null) {
            return [null, null];
        }
        $f = $this->registry->field($entity, $field);
        if ($f?->type !== FieldTypes::RELATION) {
            return [null, null];
        }
        $related = $scope->load($entity, $id)?->get($field);

        return [$f->entity, $related === null ? null : Values::text($related)];
    }

    private function createRecord(Exec $x, Token $t, CreateRecordStep $cr): void
    {
        $scope = $x->scope($t);
        $descriptor = $this->registry->entity($cr->entity) ?? throw new WorkflowException('unknownEntity', "'{$cr->entity}' is not a record type workflows know.");
        $values = new stdClass;
        foreach ($cr->values as $v) {
            $field = $descriptor->field($v->field);
            if ($field === null || ! $field->isSettable()) {
                throw new WorkflowException('fieldNotSettable', "Workflows cannot set '{$v->field}' of {$descriptor->label}.");
            }
            $values->{$v->field} = self::checkOption($field, $scope->operand($v->value));
        }
        // Lint only knows the lifecycles published when the workflow was; a new record starts in its lifecycle's first state.
        $lifecycleFields = array_map(fn ($tr) => $tr->field, array_filter($x->u->s->triggers(), fn ($tr) => $tr->type === 'lifecycle' && $tr->entity === $cr->entity));
        $owned = array_find(array_keys(get_object_vars($values)), fn ($k) => in_array((string) $k, $lifecycleFields, true));
        if ($owned !== null) {
            $label = $descriptor->field((string) $owned)?->label ?? $owned;
            throw new WorkflowException('ownedField', "{$label} is controlled by a lifecycle; a new record starts in its first state.");
        }
        $record = $this->beginStep($x, $t, $cr, (object) ['entity' => $cr->entity, 'values' => $x->u->masker($cr->entity, null)->mask($values)]);
        $id = $this->scopes->run($x->actor(), $x->i->depth + 1, $x->i->id, $x->i->definitionId, $x->i->correlationId,
            fn () => $x->u->s->entities()->create($cr->entity, $values));
        // The values it was created with (as the gateway holds them), so a reset can tell when people changed it since.
        $made = $x->u->s->entities()->get($cr->entity, $id);
        $kept = null;
        if ($made !== null) {
            $kept = new stdClass;
            foreach (get_object_vars($made->values) as $key => $value) {
                if ($descriptor->field((string) $key)?->type !== FieldTypes::COLLECTION) {
                    $kept->{$key} = Values::normalize($value);
                }
            }
        }
        $record->undo = $this->journal((object) ['kind' => 'created', 'entity' => $cr->entity, 'id' => $id, 'values' => $kept]);
        $x->s->setOutput($cr->id, 'id', $id);
        $this->completeStep($x, $record, $cr, $x->s->output($cr->id));
        $scope->invalidate();
        self::advance($t);
    }

    private function setVariables(Exec $x, Token $t, SetVariablesStep $sv): void
    {
        $scope = $x->scope($t);
        $changed = new stdClass;
        foreach ($sv->values as $v) {
            $key = $x->c->variableKey($v->variable);
            $value = Json::normalize($scope->operand($v->value));
            $x->s->vars->{$key} = $value;
            $changed->{$key} = $value;
        }
        $x->u->audit(self::stepAudit($x, $sv, 'completed', (object) ['vars' => $changed]));
        self::advance($t);
    }

    private function applyTransitionStep(Exec $x, Token $t, ApplyTransitionStep $at): void
    {
        $scope = $x->scope($t);
        [$entity, $id] = $this->target($scope, $at->target);
        if ($entity === null || $id === null) {
            throw new WorkflowException('noRecord', 'There is no record to move.');
        }
        $input = new stdClass;
        foreach ($at->input ?? [] as $key => $operand) {
            $input->{$key} = $scope->operand($operand);
        }
        $comment = $at->comment === null ? null : $this->render($scope, $at->comment);
        $record = $this->beginStep($x, $t, $at, (object) ['entity' => $entity, 'id' => $id, 'transition' => $at->transition]);
        try {
            // The named lifecycle must be the record's own (spec §6 applies to); it never starts a second one.
            $this->ensureOwnLifecycle($x->u, $this->requireLifecycle($x->u, $at->lifecycle, $entity), $entity, $id);
            $this->scopes->run($x->actor(), $x->i->depth + 1, $x->i->id, $x->i->definitionId, $x->i->correlationId,
                fn () => $this->applyTransitionCore($x->u, $at->lifecycle, $entity, $id, $at->transition, $input, $x->actor(), false, false, $x->i->depth + 1, comment: $comment));
            $applied = true;
        } catch (WorkflowException $e) {
            if ($at->ifNotAllowed !== 'skip' || ! in_array($e->errorCode, ['notInFromState', 'guardFailed', 'notAllowed', 'wrongLifecycle'], true)) {
                throw $e;
            }
            $applied = false;
        }
        $x->s->setOutput($at->id, 'applied', $applied);
        // Listed by a reset (never taken back): moving another record's lifecycle has consequences of its own.
        if ($applied) {
            $record->undo = $this->journal((object) ['kind' => 'move', 'entity' => $entity, 'id' => $id, 'lifecycle' => $at->lifecycle, 'transition' => $at->transition]);
        }
        $this->completeStep($x, $record, $at, $x->s->output($at->id));
        $scope->invalidate();
        self::advance($t);
    }

    // ── Calling out ───────────────────────────────────────────────────────

    private function notify(Exec $x, Token $t, NotifyStep $n): void
    {
        // A channel switched off in settings after the workflow was published sends nothing (lint only warned at publish).
        if (($x->u->settings->channels[$n->channel] ?? null)?->enabled === false) {
            $x->s->setOutput($n->id, 'sent', 0);
            $x->u->audit(self::stepAudit($x, $n, 'skipped', (object) ['reason' => 'channelDisabled', 'channel' => $n->channel]));
            $x->u->log?->add('step', 'skipped', $n->id, "The {$n->channel} channel is switched off.");
            self::advance($t);

            return;
        }
        $scope = $x->scope($t);
        $recipients = $this->recipients($x, $scope, $n->to);
        $subject = $n->subject === null ? null : $this->render($scope, $n->subject);
        $body = $this->render($scope, $n->body);
        $footer = ($x->u->settings->channels[$n->channel] ?? null)?->footer;
        if ($footer !== null && trim($footer) !== '') {
            $body .= "\n\n".$footer;
        }
        $link = null;
        if ($n->link !== null) {
            $link = $n->link->parts === ['record'] ? $this->recordLink($x->i->entity, $x->i->entityId) : $this->render($scope, $n->link);
        }
        $names = array_map(fn (NotificationRecipient $r) => $r->name ?? $r->email ?? $r->userId, $recipients);
        $record = $this->beginStep($x, $t, $n, (object) ['channel' => $n->channel, 'to' => $names, 'subject' => $subject]);
        $key = "{$x->i->id}:{$n->id}:{$t->id}:{$t->currentItem()[1]}:".array_sum($x->s->goTo);
        if ($recipients !== []) {
            $this->queueNotification($x->u, new WorkflowNotification($n->channel, $recipients, $subject, $body, $link, $x->i->id, $x->i->entity, $x->i->entityId, $key));
        }
        $x->s->setOutput($n->id, 'sent', count($recipients));
        // Only a message that went to someone is listed by a reset.
        if ($recipients !== []) {
            $record->undo = $this->journal((object) ['kind' => 'message', 'channel' => $n->channel, 'to' => $names, 'subject' => $subject]);
        }
        $this->completeStep($x, $record, $n, $x->s->output($n->id));
        self::advance($t);
    }

    public function recordLink(?string $entity, ?string $id): ?string
    {
        $template = $this->options->recordLinkTemplate;

        return $template === null || $entity === null || $id === null ? null : str_replace(['{entity}', '{id}'], [$entity, rawurlencode($id)], $template);
    }

    /**
     * @param  list<Assignee>  $to
     * @return list<NotificationRecipient>
     */
    public function recipients(Exec $x, ValueScope $scope, array $to): array
    {
        $list = [];
        foreach ($this->resolveAssignees($scope, array_values(array_filter($to, fn (Assignee $a) => $a->email === null)), null, $x->i->initiatorId) as $id) {
            $user = $this->options->identity->user($id);
            $list[] = new NotificationRecipient($id, $user?->email, $user->name ?? $id);
        }
        foreach ($to as $a) {
            if ($a->email === null) {
                continue;
            }
            foreach (preg_split('/[,;]/', $this->render($scope, $a->email)) as $address) {
                $address = trim($address);
                if ($address !== '' && str_contains($address, '@')) {
                    $list[] = new NotificationRecipient(null, $address, $address);
                }
            }
        }
        $seen = [];

        return array_values(array_filter($list, function (NotificationRecipient $r) use (&$seen) {
            $key = $r->userId ?? $r->email;
            if (isset($seen[$key])) {
                return false;
            }

            return $seen[$key] = true;
        }));
    }

    /**
     * The address, headers and body of a webhook call. The address must stay on the named connection's host or match
     * an allowed origin; values from records go into the path URL-encoded, so they cannot add segments, a query or a host.
     *
     * @return array{0: string, 1: array<string, string>, 2: ?string}
     */
    private function buildWebhook(Exec $x, Token $t, WebhookStep $wh): array
    {
        $scope = $x->scope($t);
        if (! in_array($wh->method, WebhookStep::METHODS, true)) {
            throw new WorkflowException('badMethod', "'{$wh->method}' is not an HTTP method a webhook can use.");
        }
        $headers = [];
        $path = $wh->path === null ? '' : $this->render($scope, $wh->path, rawurlencode(...));
        // "." and ".." segments (also percent-encoded) would climb out of the connection's base path once resolved.
        $pathOnly = preg_split('/[?#]/', $path)[0];
        if (array_any(explode('/', $pathOnly), fn ($segment) => in_array(rawurldecode($segment), ['.', '..'], true))) {
            throw new WorkflowException('webhookOriginNotAllowed', "The webhook path cannot contain '.' or '..' segments.");
        }
        if ($wh->connection !== null) {
            $connection = array_find($x->u->settings->webhooks->connections ?? [], fn ($c) => $c->key === $wh->connection)
                ?? throw new WorkflowException('unknownConnection', "There is no connection called '{$wh->connection}'.");
            $url = rtrim($connection->baseUrl, '/').'/'.ltrim($path, '/');
            if ($connection->auth === 'secret' && $connection->secretName !== null && ! $x->u->simulation) {
                $secret = $this->options->secrets->secret($connection->secretName)
                    ?? throw new WorkflowException('missingSecret', "The secret for '{$connection->label}' is not set up.");
                $headers[$connection->header ?? 'Authorization'] = $connection->header === null ? "Bearer {$secret}" : $secret;
            }
            $base = AbsoluteUrl::tryParse($connection->baseUrl);
            $target = AbsoluteUrl::tryParse($url);
            $basePath = rtrim(self::urlPath($connection->baseUrl), '/').'/';
            if ($base === null || $target === null || $target->authority() !== $base->authority() || $target->scheme !== $base->scheme
                || ! str_starts_with(self::urlPath($url).'/', $basePath)) {
                throw new WorkflowException('webhookOriginNotAllowed', "The webhook path must stay on the connection's address.");
            }
        } else {
            $url = ($wh->url ?? '').$path;
            $uri = AbsoluteUrl::tryParse($url);
            if ($uri === null || ! $uri->isHttp()) {
                throw new WorkflowException('webhookOriginNotAllowed', "'{$url}' is not a web address.");
            }
            $origin = $uri->origin();
            if (! array_any($x->u->settings->webhooks->allowedOrigins ?? [], fn ($o) => strcasecmp($o, $origin) === 0)) {
                throw new WorkflowException('webhookOriginNotAllowed', "{$origin} is not an allowed address.");
            }
        }
        foreach ($wh->headers ?? [] as $name => $template) {
            $name = (string) $name;
            $value = $this->render($scope, $template);
            // A line break in a value would add headers of its own (values come from records).
            if (! self::isValidHeader($name, $value)) {
                throw new WorkflowException('badHeader', "The header '{$name}' cannot be sent: its name must be a plain token and its value cannot contain line breaks or other control characters.");
            }
            foreach (array_keys($headers) as $existing) {
                if (strcasecmp($existing, $name) === 0) {
                    unset($headers[$existing]);
                }
            }
            $headers[$name] = $value;
        }
        $body = $wh->body === null ? null : Json::encode($this->substitute($scope, Json::copy($wh->body)));

        return [$url, $headers, $body];
    }

    /** The absolute path of a URL (`/` when it has none), as .NET's `Uri.AbsolutePath`. */
    private static function urlPath(string $url): string
    {
        $path = parse_url($url, PHP_URL_PATH);

        return is_string($path) && $path !== '' ? $path : '/';
    }

    /** Whether a header name is a plain token and its value has no line breaks or other control characters (.NET `HttpWebhookClient.IsValidHeader`). */
    public static function isValidHeader(string $name, string $value): bool
    {
        return preg_match('/^[!#$%&\'*+\-.^_`|~0-9A-Za-z]+$/', $name) === 1 && preg_match('/[\x00-\x08\x0A-\x1F\x7F]/', $value) !== 1;
    }

    /** Replaces `{"ref": "path"}` objects in a webhook body with their values. */
    private function substitute(ValueScope $scope, mixed $node): mixed
    {
        if ($node instanceof stdClass) {
            $members = get_object_vars($node);
            if (count($members) === 1 && is_string($members['ref'] ?? null)) {
                return $scope->resolve($members['ref']);
            }
            foreach ($members as $key => $value) {
                $node->{$key} = $this->substitute($scope, $value);
            }

            return $node;
        }
        if (is_array($node)) {
            return array_map(fn ($item) => $this->substitute($scope, $item), $node);
        }

        return $node;
    }

    private function webhook(Exec $x, Token $t, WebhookStep $wh): void
    {
        [$url, $headers, $body] = $this->buildWebhook($x, $t, $wh);
        // The same for every attempt of this run of the step (retries resend it; the receiver drops repeats), new when a
        // forEach item or a goTo repeat runs the step again.
        $key = "{$x->i->id}:{$wh->id}:{$t->id}:{$t->currentItem()[1]}:".array_sum($x->s->goTo);
        $record = $this->beginStep($x, $t, $wh, (object) ['method' => $wh->method, 'url' => $url]);
        if ($x->u->simulation) {
            $x->u->log?->add('webhook', $wh->method, $x->i->id, $url);
            $response = new WebhookResponse(200, null);
        } else {
            $timeout = $wh->timeout === null ? 30000 : IsoDuration::parse($wh->timeout)->approximateMs();
            try {
                $client = $this->options->webhooks ?? throw new WebhookFailedException('No webhook client is set up.');
                $response = $client->send(new WebhookRequest($wh->method, $url, $headers, $body, $key, $timeout));
            } catch (WebhookFailedException $e) {
                // A failed call fails the attempt (and may be retried).
                $this->handleStepError($x, $t, $wh, $e, $record);

                return;
            }
        }
        $x->s->setOutput($wh->id, 'status', $response->status);
        $parsed = $response->body;
        if ($response->body !== null && $response->body !== '') {
            try {
                $parsed = Json::decode($response->body);
            } catch (JsonException) {
                $parsed = $response->body;
            }
        }
        $x->s->setOutput($wh->id, 'body', $parsed);
        $ok = $wh->expect !== null && $wh->expect !== [] ? in_array($response->status, $wh->expect, true) : $response->status >= 200 && $response->status < 300;
        if (! $ok) {
            $this->handleStepError($x, $t, $wh, new WorkflowException('webhookFailed', "The webhook answered {$response->status}."), $record);

            return;
        }
        $record->undo = $this->journal((object) ['kind' => 'webhook', 'method' => $wh->method, 'url' => $url]);
        $this->completeStep($x, $record, $wh, $x->s->output($wh->id));
        self::advance($t);
    }

    private function action(Exec $x, Token $t, ActionStep $a): void
    {
        $descriptor = $this->registry->action($a->action) ?? throw new WorkflowException('unknownAction', "'{$a->action}' is not an action the app offers.");
        $scope = $x->scope($t);
        $input = new stdClass;
        foreach ($a->input ?? [] as $key => $operand) {
            $input->{$key} = Json::normalize($scope->operand($operand));
        }
        foreach ($descriptor->inputs as $p) {
            if ($p->required === true && Values::isEmpty($input->{$p->key} ?? null)) {
                throw new WorkflowException('missingInput', "'{$descriptor->label}' needs {$p->label}.");
            }
        }
        $record = $this->beginStep($x, $t, $a, $input);
        if ($x->u->simulation) {
            $x->u->log?->add('action', $a->action, $x->i->id, $descriptor->label, $input);
            $outputs = new stdClass;
            foreach ($descriptor->outputs as $o) {
                $outputs->{$o->key} = null;
            }
        } else {
            $handler = $this->options->actions->resolve($descriptor) ?? throw new WorkflowException('actionUnavailable', "No handler is registered for '{$descriptor->label}'.");
            $outputs = Json::normalize($this->runAction($x, $t, $a, $handler, $input));
        }
        foreach (get_object_vars($outputs) as $key => $value) {
            $x->s->setOutput($a->id, (string) $key, $value);
        }
        $record->undo = $this->journal((object) ['kind' => 'action', 'action' => $a->action, 'input' => $input, 'output' => $outputs]);
        $this->completeStep($x, $record, $a, $x->s->output($a->id));
        $scope->invalidate();
        self::advance($t);
    }

    /**
     * Calls an action handler inside a savepoint: if it fails, whatever it wrote is undone before the step fails
     * (spec §7: a failed step changes nothing).
     */
    private function runAction(Exec $x, Token $t, ActionStep $a, WorkflowAction $handler, stdClass $input): stdClass
    {
        $savepoint = 'wf_action_'.self::newId();
        $undoable = $x->u->s->createSavepoint($savepoint);
        try {
            $outputs = $this->scopes->run($x->actor(), $x->i->depth + 1, $x->i->id, $x->i->definitionId, $x->i->correlationId,
                fn () => $handler->execute(new ActionContext($a->action, Json::copy($input), $x->i->entity, $x->i->entityId, $x->i->id,
                    "{$x->i->id}:{$a->id}:{$t->id}:{$t->currentItem()[1]}:".array_sum($x->s->goTo), $t->top()->attempt,
                    $x->i->initiatorId === null ? null : WorkflowActor::user($x->i->initiatorId, $x->i->initiatorName))));
            if ($undoable) {
                $x->u->s->releaseSavepoint($savepoint);
            }

            return $outputs;
        } catch (WorkflowConcurrencyException $e) {
            throw $e;
        } catch (Throwable $e) {
            if ($undoable) {
                $x->u->s->rollbackToSavepoint($savepoint);
            }
            throw $e;
        } finally {
            if ($x->i->entity !== null && $x->i->entityId !== null) {
                $x->u->forgetMasker($x->i->entity, $x->i->entityId);
            }
        }
    }

    private function subWorkflow(Exec $x, Token $t, SubWorkflowStep $sub): void
    {
        $definition = $x->u->s->definitionByKey($sub->workflow);
        if ($definition === null || $definition->version === 0 || $definition->status === DefinitionStatus::ARCHIVED) {
            throw new WorkflowException('unknownWorkflow', "There is no published workflow called '{$sub->workflow}'.");
        }
        $child = $this->compiled($x->u, $definition->id, $definition->version);
        // Loop protection (spec §5): workflows that run each other would otherwise go on forever inside one unit of work.
        $max = $x->u->settings->maxChainDepth();
        if ($x->i->depth + 1 >= $max) {
            throw new WorkflowException('subWorkflowDepth',
                "'{$sub->workflow}' was not started: workflows starting each other reached the limit of {$max} levels (maxChainDepth in settings). Check for workflows that run each other in a loop.");
        }
        $scope = $x->scope($t);
        [$entity, $id] = $this->target($scope, $sub->target);
        $input = new stdClass;
        foreach ($sub->input ?? [] as $key => $operand) {
            $input->{$key} = Json::normalize($scope->operand($operand));
        }
        $record = $this->beginStep($x, $t, $sub, (object) ['workflow' => $sub->workflow, 'wait' => $sub->wait]);
        $trigger = new TriggerInfo('subWorkflow', actorType: 'workflow', actorId: $x->i->id, actorName: $x->i->definitionKey, at: Instant::format($x->u->now));
        $bookmarkId = self::newId();
        // The child is created now but runs after this instance has been saved (deferred), so a child that finishes
        // straight away resumes a parent that is already waiting for it.
        $started = $this->startAutomation($x->u, $child, $entity, $id, $trigger, $x->actor(), $x->i->depth + 1, $input,
            $sub->wait ? $x->i->id : null, $sub->wait ? "sub:{$sub->id}:{$t->id}:{$bookmarkId}" : null, $x->i->correlationId, drive: false);
        $x->s->setOutput($sub->id, 'instanceId', $started?->id);
        if ($started === null) {
            if ($sub->wait) {
                throw new WorkflowException('subWorkflowSkipped', "'{$sub->workflow}' did not start (it is already running for this record).");
            }
            $x->s->setOutput($sub->id, 'status', 'skipped');
            $this->completeStep($x, $record, $sub, $x->s->output($sub->id));
            self::advance($t);

            return;
        }
        if ($started->status !== InstanceStatus::QUEUED) {
            $x->u->deferred[] = $started->id;
        }
        if (! $sub->wait) {
            $x->s->setOutput($sub->id, 'status', $started->status);
            $this->completeStep($x, $record, $sub, $x->s->output($sub->id));
            self::advance($t);

            return;
        }
        $b = new BookmarkRecord;
        $b->id = $bookmarkId;
        $b->instanceId = $x->i->id;
        $b->tokenId = $t->id;
        $b->nodeId = $sub->id;
        $b->kind = BookmarkKinds::CHILD;
        $b->purpose = BookmarkPurposes::CHILD;
        $b->payload = $started->id;
        $b->createdAt = $x->u->now;
        $x->u->s->addBookmark($b);
        self::wait($t, "child:{$started->id}", $record);
        $x->u->s->updateStep($record);
    }

    /** When a waited-for sub-workflow finishes, its parent step continues. */
    public function notifyParent(Uow $u, InstanceRecord $child, RunState $childState): void
    {
        if ($child->kind !== InstanceKinds::AUTOMATION || $child->parentId === null || ! str_starts_with($child->reason ?? '', 'sub:')) {
            return;
        }
        [, $stepId, $tokenId, $bookmarkId] = explode(':', $child->reason);
        if ($u->s->bookmark($bookmarkId) === null) {
            return;
        }
        $u->s->removeBookmark($bookmarkId);
        $x = $this->loadExec($u, $child->parentId);
        $t = $x->s->token($tokenId);
        $step = $x->c->step($stepId);
        if ($t === null || $step === null || $t->status !== Token::WAITING) {
            return;
        }
        $x->s->setOutput($stepId, 'instanceId', $child->id);
        $x->s->setOutput($stepId, 'status', $child->status);
        $x->s->setOutput($stepId, 'outputs', $childState->vars);
        self::resume($t);
        if ($child->status !== InstanceStatus::COMPLETED) {
            $this->handleStepError($x, $t, $step, new WorkflowException('subWorkflowFailed', "'{$child->definitionKey}' {$child->status}: {$child->error}"), null);
        } else {
            $this->completeWaitingStep($x, $t, $step);
            self::advance($t);
        }
        $this->drive($x);
    }

    // ── Errors ────────────────────────────────────────────────────────────

    public function handleStepError(Exec $x, Token $t, Step $step, Throwable $error, ?StepRecord $record): void
    {
        $message = self::describeError($error);
        $attempt = $t->top()->attempt;
        $record ??= $x->current !== null && $x->current->nodeId === $step->id ? $x->current : null;
        $record ??= $t->stepRecord === null ? null : array_find($x->u->s->steps($x->i->id), fn (StepRecord $s) => $s->id === $t->stepRecord);
        $x->current = null;
        $t->stepRecord = null;
        if ($record !== null) {
            $record->status = StepStatus::FAILED;
            $record->error = $message;
            $record->endedAt = $x->u->now;
            $x->u->s->updateStep($record);
        }
        $x->u->audit(self::stepAudit($x, $step, 'failed', (object) ['error' => $message, 'attempt' => $attempt]));
        $x->u->log?->add('step', 'failed', $step->id, $message);
        $retry = $step->retry ?? ($step instanceof ActionStep || $step instanceof WebhookStep || $step instanceof NotifyStep || $step instanceof SubWorkflowStep
            ? $x->u->settings->defaults?->retry : null);
        $retryable = ! ($error instanceof WorkflowException && in_array($error->errorCode, self::NOT_RETRYABLE, true));
        if ($retry !== null && $retry->max > 0 && $attempt <= $retry->max && $retryable) {
            $initial = $retry->initial === null ? 60000 : IsoDuration::parse($retry->initial)->approximateMs();
            $delay = $retry->backoff === 'exponential' ? $initial * (1 << min($attempt - 1, 20)) : $initial;
            if ($retry->maxDelay !== null) {
                $delay = min($delay, IsoDuration::parse($retry->maxDelay)->approximateMs());
            }
            $t->top()->attempt = $attempt + 1;
            $this->addTimer($x, $t, $step, BookmarkPurposes::RETRY, $x->u->now + $delay, null);
            self::wait($t, 'retry', null);
            $x->u->audit(self::stepAudit($x, $step, 'retryScheduled', (object) ['attempt' => $attempt + 1, 'at' => Instant::format($x->u->now + $delay)]));

            return;
        }
        switch ($step->onError?->mode ?? 'stop') {
            case 'continue':
                $x->s->setOutput($step->id, 'error', $message);
                self::advance($t);
                break;
            case 'steps':
                $x->s->setOutput($step->id, 'error', $message);
                $t->frames[] = new Frame("{$step->id}/error");
                break;
            default:
                $this->raiseIncident($x, $t, $step, $message);
                break;
        }
    }

    public function raiseIncident(Exec $x, Token $t, ?Step $step, string $message): void
    {
        self::wait($t, 'incident', null);
        $t->incidentError = $message;
        $t->incidentStepId = $step?->id;
        $x->i->status = InstanceStatus::INCIDENT;
        $x->i->error = $message;
        $x->i->errorStepId = $step?->id;
        $x->u->audit(new AuditEntry('instance', 'incident', 'instance', $x->i->id, $x->actor(), $x->i->entity, $x->i->entityId, $x->i->id, $x->i->definitionId,
            data: (object) ['step' => $step?->id, 'error' => $message, 'token' => $t->id], correlationId: $x->i->correlationId));
        $x->u->log?->add('instance', 'incident', $x->i->id, $message);
        $owners = $x->c->definition->settings;
        if ($owners?->notifyOwnersOnIncident === true && $owners->owners !== null && $owners->owners !== []) {
            $people = $this->recipients($x, $x->scope($t), $owners->owners);
            $channel = $this->options->taskNotificationChannel ?? array_find($this->registry->channels, fn ($c) => in_array($c->key, ['inApp', 'email'], true))?->key;
            if ($channel !== null && $people !== [] && ($x->u->settings->channels[$channel] ?? null)?->enabled !== false) {
                $title = $x->c->definition->title;
                $this->queueNotification($x->u, new WorkflowNotification($channel, $people, "Workflow needs attention: {$title}",
                    "“{$title}” stopped at “".($step->title ?? $step?->type)."”: {$message}", $this->recordLink($x->i->entity, $x->i->entityId), $x->i->id,
                    $x->i->entity, $x->i->entityId, "{$x->i->id}:incident:{$step?->id}:{$t->top()->attempt}"));
            }
        }
    }
}
