<?php

namespace Zouriel\Workflows\Core\Lint;

use stdClass;
use Zouriel\Forms\Conditions;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\ActionStep;
use Zouriel\Workflows\Core\Model\ApplyTransitionStep;
use Zouriel\Workflows\Core\Model\ApprovalStep;
use Zouriel\Workflows\Core\Model\Assignee;
use Zouriel\Workflows\Core\Model\BranchStep;
use Zouriel\Workflows\Core\Model\Condition;
use Zouriel\Workflows\Core\Model\ConditionGroup;
use Zouriel\Workflows\Core\Model\ConditionRule;
use Zouriel\Workflows\Core\Model\CreateRecordStep;
use Zouriel\Workflows\Core\Model\EndStep;
use Zouriel\Workflows\Core\Model\ForEachStep;
use Zouriel\Workflows\Core\Model\GoToStep;
use Zouriel\Workflows\Core\Model\HumanStep;
use Zouriel\Workflows\Core\Model\NotifyStep;
use Zouriel\Workflows\Core\Model\Operand;
use Zouriel\Workflows\Core\Model\ParallelStep;
use Zouriel\Workflows\Core\Model\SetFieldsStep;
use Zouriel\Workflows\Core\Model\SetVariablesStep;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Model\Step;
use Zouriel\Workflows\Core\Model\SubWorkflowStep;
use Zouriel\Workflows\Core\Model\SwitchStep;
use Zouriel\Workflows\Core\Model\TaskOutcome;
use Zouriel\Workflows\Core\Model\TaskStep;
use Zouriel\Workflows\Core\Model\Template;
use Zouriel\Workflows\Core\Model\Transition;
use Zouriel\Workflows\Core\Model\TriggerTypes;
use Zouriel\Workflows\Core\Model\UnknownStep;
use Zouriel\Workflows\Core\Model\Variable;
use Zouriel\Workflows\Core\Model\WaitStep;
use Zouriel\Workflows\Core\Model\WebhookStep;
use Zouriel\Workflows\Core\Model\WorkflowDefinition;
use Zouriel\Workflows\Core\Registry\EntityDescriptor;
use Zouriel\Workflows\Core\Registry\FieldDescriptor;
use Zouriel\Workflows\Core\Registry\FieldOption;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Registry\LabelledItem;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\WebhookConnection;
use Zouriel\Workflows\Core\Support\AbsoluteUrl;
use Zouriel\Workflows\Core\Time\Cron;
use Zouriel\Workflows\Core\Time\IsoDuration;

/**
 * One lint pass over one definition: the body of .NET `Lint/Linter.cs` (`Linter.Run`), method for
 * method, with the same messages.
 *
 * @internal use `Linter::lint()`
 */
final class LintRun
{
    /** @var list<LintProblem> */
    private array $problems = [];

    /** @var array<string, true> */
    private array $ids = [];

    /** @var array<string, Step> */
    private array $allSteps = [];

    private readonly CompiledDefinition $compiled;

    private readonly WorkflowRegistry $r;

    /** @var array<string, list<string>>|null workflow key → the workflow keys its sub-workflow steps run */
    private ?array $subGraph = null;

    public function __construct(private readonly WorkflowDefinition $def, private readonly LintContext $ctx)
    {
        $this->compiled = new CompiledDefinition($def, $def->id, 0);
        $this->r = $ctx->registry;
    }

    /** @return list<LintProblem> */
    public function go(): array
    {
        $def = $this->def;
        if ($def->specVersion !== 1) {
            $this->error('specVersion', null, "This engine reads spec version 1; the definition says {$def->specVersion}.");
        }
        if (self::blank($def->title)) {
            $this->error('missingTitle', null, 'Give the workflow a name.');
        }
        $entity = $def->entity === null ? null : $this->r->entity($def->entity);
        if ($def->entity !== null && $entity === null) {
            $this->error('unknownEntity', null, "'{$def->entity}' is not a record type the app allows workflows to use.");
        }
        foreach ($def->variables ?? [] as $v) {
            $this->checkId($v->id, $v->id);
            if (count(array_filter($def->variables, fn (Variable $x) => $x->key === $v->key)) > 1) {
                $this->error('duplicateKey', $v->id, "Two variables are called '{$v->key}'.");
            }
        }
        if ($def->publicProcessingTime !== null) {
            $ppt = $def->publicProcessingTime;
            if ($ppt->display && self::blank($ppt->estimate)) {
                $this->error('publicTimeMissing', null, 'Write the processing time people will see, or turn it off.');
            }
            if (Js::length($ppt->estimate) > 200) {
                $this->error('publicTimeTooLong', null, 'The processing time people will see can be at most 200 characters.');
            }
        }
        if ($def->isLifecycle()) {
            $this->lintLifecycle($entity);
        } else {
            $this->lintAutomation($entity);
        }

        return $this->problems;
    }

    private function error(string $code, ?string $node, string $message): void
    {
        $this->problems[] = new LintProblem($code, 'error', $node, $message);
    }

    private function warn(string $code, ?string $node, string $message): void
    {
        $this->problems[] = new LintProblem($code, 'warning', $node, $message);
    }

    private static function name(Step $s): string
    {
        return self::blank($s->title) ? (Linter::STEP_LABELS[$s->type] ?? $s->type) : $s->title;
    }

    /** `string.IsNullOrWhiteSpace`. */
    private static function blank(?string $text): bool
    {
        return $text === null || Js::trim($text) === '';
    }

    private function checkId(string $id, ?string $node): void
    {
        if ($id === '') {
            $this->error('missingId', $node, 'An item has no id.');
        } elseif (isset($this->ids[$id])) {
            $this->error('duplicateId', $node, "The id '{$id}' is used twice.");
        } else {
            $this->ids[$id] = true;
        }
    }

    // ── Automations ───────────────────────────────────────────────────

    private function lintAutomation(?EntityDescriptor $entity): void
    {
        $def = $this->def;
        $t = $def->trigger;
        if ($t === null) {
            $this->error('missingTrigger', null, 'Choose what starts this workflow.');

            return;
        }
        // Schedules (without "for each record"), app-wide signals and manual buttons can run without a record.
        $needsEntity = match ($t->type) {
            TriggerTypes::SCHEDULE => $t->forEach !== null,
            TriggerTypes::SIGNAL => $this->r->signal($t->signal)?->entity !== null,
            TriggerTypes::MANUAL => false,
            default => true,
        };
        if ($needsEntity && $def->entity === null) {
            $this->error('missingEntity', null, 'Choose which record type this workflow is about.');
        }
        if ($t->type === TriggerTypes::SCHEDULE && $t->forEach === null && $def->entity !== null) {
            $this->warn('scheduleEntity', null, "A schedule without 'for each record' runs without a record; the record type is ignored.");
        }
        switch ($t->type) {
            case TriggerTypes::CREATED:
            case TriggerTypes::DELETED:
                break;
            case TriggerTypes::FIELD_CHANGED:
                $f = $entity?->field($t->field);
                if ($t->field === null || $t->field === '') {
                    $this->error('missingField', null, 'Choose which field to watch.');
                } elseif ($entity !== null && $f === null) {
                    $this->error('unknownField', null, "'{$t->field}' is not a field of {$entity->label}.");
                } elseif ($f !== null && ! $f->isWatchable()) {
                    $this->error('fieldNotWatchable', null, "{$f->label} cannot be watched; the app has not allowed it.");
                }
                if ($f?->options !== null) {
                    foreach ([...$t->to ?? [], ...$t->from ?? []] as $v) {
                        $text = Values::text(Json::normalize($v));
                        if (! $this->isOption($f->options, $text)) {
                            $this->error('unknownValue', null, "'{$text}' is not one of {$f->label}'s values.");
                        }
                    }
                }
                break;
            case TriggerTypes::STATE_ENTERED:
            case TriggerTypes::TRANSITION_APPLIED:
                $lc = array_find($this->ctx->otherPublished, fn (WorkflowDefinition $d) => $d->isLifecycle() && $d->key === $t->lifecycle);
                if ($lc === null) {
                    $this->error('unknownLifecycle', null, "There is no published lifecycle called '{$t->lifecycle}'.");
                    break;
                }
                if ($lc->entity !== $def->entity) {
                    $this->error('lifecycleEntity', null, "The lifecycle '{$lc->title}' is about a different record type.");
                }
                if ($t->type === TriggerTypes::STATE_ENTERED) {
                    if (($t->states ?? []) === []) {
                        $this->error('missingStates', null, 'Choose which states start the workflow.');
                    }
                    foreach ($t->states ?? [] as $s) {
                        if (! array_any($lc->lifecycle?->states ?? [], fn (State $x) => $x->key === $s)) {
                            $this->error('unknownState', null, "'{$s}' is not a state of {$lc->title}.");
                        }
                    }
                } else {
                    if (($t->transitions ?? []) === []) {
                        $this->error('missingTransitions', null, 'Choose which transitions start the workflow.');
                    }
                    foreach ($t->transitions ?? [] as $s) {
                        if (! array_any($lc->lifecycle?->transitions ?? [], fn (Transition $x) => $x->key === $s)) {
                            $this->error('unknownTransition', null, "'{$s}' is not a transition of {$lc->title}.");
                        }
                    }
                }
                break;
            case TriggerTypes::DATE_REACHED:
                $df = $entity?->field($t->field);
                if ($df === null) {
                    $this->error('unknownField', null, 'Choose the date field.');
                } elseif ($df->type !== FieldTypes::DATE && $df->type !== FieldTypes::DATE_TIME) {
                    $this->error('notADate', null, "{$df->label} is not a date.");
                } elseif (! $df->isWatchable()) {
                    $this->error('fieldNotWatchable', null, "{$df->label} cannot be watched; the app has not allowed it.");
                }
                if (IsoDuration::tryParse($t->offset) === null) {
                    $this->error('badDuration', null, "'{$t->offset}' is not a valid time offset.");
                }
                break;
            case TriggerTypes::SCHEDULE:
                [$cron, $cronError] = Cron::tryParse($t->cron);
                if ($cron === null) {
                    $this->error('badCron', null, $cronError ?? 'The schedule is not valid.');
                }
                if ($t->forEach !== null && ($t->forEach->limit < 1 || $t->forEach->limit > 500)) {
                    $this->error('badLimit', null, 'Scheduled runs can cover 1 to 500 records.');
                }
                break;
            case TriggerTypes::MANUAL:
                if (self::blank($t->label)) {
                    $this->error('missingLabel', null, 'Give the button a label.');
                }
                $this->checkAssignees($t->who, null, true);
                break;
            case TriggerTypes::SIGNAL:
                $sig = $this->r->signal($t->signal);
                if ($sig === null) {
                    $this->error('unknownSignal', null, "'{$t->signal}' is not an event the app sends.");
                } elseif ($sig->entity !== null && $sig->entity !== $def->entity) {
                    $this->error('signalEntity', null, "'{$sig->label}' is about a different record type.");
                }
                break;
            default:
                $this->error('unknownTrigger', null, "'{$t->type}' is not a trigger this engine knows.");
                break;
        }
        if ($t->when !== null) {
            $this->checkCondition($t->when, null, new LintEnv($entity?->key, null, [], false, $t));
        }
        if (($def->steps ?? []) === []) {
            $this->warn('emptyWorkflow', null, 'The workflow has no steps yet.');
        }
        // input.* is always allowed in automations: manual triggers and sub-workflow calls both pass input.
        $this->lintSteps($def->steps ?? [], new LintEnv($entity?->key, null, [], true, $t), 'root');
        $this->checkLoops($entity);
    }

    /** A workflow that writes a field its own trigger watches starts itself again unless it opts in. */
    private function checkLoops(?EntityDescriptor $entity): void
    {
        $t = $this->def->trigger;
        if ($t === null || $entity === null || $t->retrigger === true) {
            return;
        }
        foreach ($this->allSteps as $s) {
            if (! $s instanceof SetFieldsStep || ($s->target !== null && $s->target !== 'self')) {
                continue;
            }
            if ($t->type === TriggerTypes::FIELD_CHANGED && array_any($s->values, fn ($v) => $v->field === $t->field)) {
                $this->warn('triggerLoop', $s->id, "'".self::name($s)."' changes {$t->field}, which starts this workflow. It will not start itself again (turn on 're-trigger' to allow it).");
            }
        }
    }

    // ── Lifecycles ────────────────────────────────────────────────────

    private function lintLifecycle(?EntityDescriptor $entity): void
    {
        $def = $this->def;
        $lc = $def->lifecycle;
        if ($lc === null) {
            $this->error('missingLifecycle', null, 'Add the states of the lifecycle.');

            return;
        }
        if ($def->entity === null) {
            $this->error('missingEntity', null, 'Choose which record type this lifecycle is for.');
        }
        $field = $entity?->field($lc->field);
        if ($entity !== null && $field === null) {
            $this->error('unknownField', null, "'{$lc->field}' is not a field of {$entity->label}.");
        } elseif ($field !== null && ! $field->isLifecycle()) {
            $this->error('notLifecycleField', null, "{$field->label} cannot be controlled by a lifecycle; the app has not allowed it.");
        }
        // Several lifecycles may share a field when "applies to" tells them apart (spec §6): at most one
        // without a condition (the fallback for every other record).
        foreach ($this->ctx->otherPublished as $other) {
            if (! $other->isLifecycle() || $other->id === $def->id || $other->entity !== $def->entity || $other->lifecycle?->field !== $lc->field) {
                continue;
            }
            $theirs = $other->lifecycle->appliesWhen;
            if ($lc->appliesWhen === null && $theirs === null) {
                $this->error('lifecycleConflict', null, "'{$other->title}' already controls this field for every record. Give one of them an “Applies to” condition.");
            } elseif ($lc->appliesWhen !== null && $theirs !== null && ! self::clearlyApart($lc->appliesWhen, $theirs)) {
                $this->warn('lifecyclesMayOverlap', null, "'{$other->title}' may apply to the same records; a record gets the lifecycle published first.");
            }
        }
        if ($lc->appliesWhen !== null) {
            $this->checkCondition($lc->appliesWhen, null, new LintEnv($entity?->key, null, [], false, null));
            foreach ($lc->appliesWhen->refs() as $ref) {
                $parts = explode('.', $ref);
                if (count($parts) !== 2 || $parts[0] !== 'entity') {
                    $this->error('appliesWhenField', null, '“Applies to” can only read this record\'s own fields (not related records, the trigger or steps).');
                } elseif ($parts[1] === $lc->field) {
                    $this->error('appliesWhenField', null, '“Applies to” cannot read the field the lifecycle controls.');
                }
            }
        }

        if ($lc->states === []) {
            $this->error('noStates', null, 'Add at least one state.');
        }
        $initials = count(array_filter($lc->states, fn (State $s) => $s->isInitial()));
        if ($initials === 0) {
            $this->error('noInitialState', null, 'Mark one state as the starting state.');
        }
        if ($initials > 1) {
            $this->error('multipleInitialStates', null, 'Only one state can be the starting state.');
        }
        foreach ($lc->states as $s) {
            $this->checkId($s->id, $s->id);
            if (self::blank($s->label)) {
                $this->error('missingLabel', $s->id, 'Give every state a name.');
            }
            if (count(array_filter($lc->states, fn (State $x) => $x->key === $s->key)) > 1) {
                $this->error('duplicateKey', $s->id, "Two states use the value '{$s->key}'.");
            }
            if ($field?->options !== null && ! $this->isOption($field->options, $s->key)) {
                $this->error('unknownValue', $s->id, "'{$s->key}' is not one of {$field->label}'s values.");
            }
            $env = new LintEnv($entity?->key, null, [], false, null);
            // Entering a state by a transition with a form passes its answers on (input.*).
            if ($s->onEnter !== null) {
                $this->lintSteps($s->onEnter, $env->with(inputAllowed: true), "onEnter:{$s->id}");
            }
            if ($s->onExit !== null) {
                $this->lintSteps($s->onExit, $env, "onExit:{$s->id}");
            }
            if ($s->sla !== null) {
                $sla = $s->sla;
                if (IsoDuration::tryParse($sla->within) === null) {
                    $this->error('badDuration', $s->id, "'{$sla->within}' is not a valid time limit.");
                }
                $this->lintSteps($sla->steps, $env, "sla:{$s->id}");
                if ($s->isTerminal()) {
                    $this->warn('slaOnTerminal', $s->id, "A time limit on the final state '{$s->label}' is never checked.");
                }
                foreach ($sla->pauseOnStatus ?? [] as $pid) {
                    if ($pid === $s->id) {
                        $this->error('slaPauseSelf', $s->id, "The clock of '{$s->label}' cannot pause while the record is in '{$s->label}' itself.");
                    } elseif (! array_any($lc->states, fn (State $x) => $x->id === $pid)) {
                        $this->error('unknownState', $s->id, "The time limit of '{$s->label}' pauses in a state that does not exist.");
                    }
                }
                foreach (array_unique([...$sla->pauseOnEvent ?? [], ...$sla->resumeOnEvent ?? []]) as $key) {
                    if ($this->r->signal($key) === null) {
                        $this->error('unknownSignal', $s->id, "'{$key}' is not an event the app sends.");
                    }
                }
                if (($sla->pauseOnEvent ?? []) !== [] && ($sla->resumeOnEvent ?? []) === []) {
                    $this->warn('slaNeverResumes', $s->id, 'The clock is paused by an event but nothing starts it again.');
                }
            }
        }
        $stateIds = array_map(fn (State $s) => $s->id, $lc->states);
        $isState = fn (?string $id) => in_array($id, $stateIds, true);
        foreach ($lc->transitions as $t) {
            $this->checkId($t->id, $t->id);
            if (self::blank($t->label)) {
                $this->error('missingLabel', $t->id, 'Give every transition a name.');
            }
            if (count(array_filter($lc->transitions, fn (Transition $x) => $x->key === $t->key)) > 1) {
                $this->error('duplicateKey', $t->id, "Two transitions use the key '{$t->key}'.");
            }
            if (! $isState($t->to)) {
                $this->error('unknownState', $t->id, "'{$t->label}' goes to a state that does not exist.");
            }
            if (! $t->from->any) {
                if ($t->from->states === []) {
                    $this->error('missingFrom', $t->id, "Choose which states '{$t->label}' starts from.");
                }
                foreach ($t->from->states as $f) {
                    if (! $isState($f)) {
                        $this->error('unknownState', $t->id, "'{$t->label}' starts from a state that does not exist.");
                    }
                }
                if (in_array($t->to, $t->from->states, true)) {
                    $this->warn('selfTransition', $t->id, "'{$t->label}' starts and ends in the same state.");
                }
                foreach ($t->from->states as $f) {
                    $term = array_find($lc->states, fn (State $s) => $s->id === $f);
                    if ($term?->isTerminal()) {
                        $this->warn('fromTerminal', $t->id, "'{$t->label}' starts from the final state '{$term->label}'.");
                    }
                }
            }
            $this->checkAssignees($t->who, $t->id, true);
            if (($t->who ?? []) === [] && $t->auto === null) {
                $this->warn('nobodyCanApply', $t->id, "Nobody can apply '{$t->label}' by hand and it is not automatic; only other workflows can.");
            }
            $env = new LintEnv($entity?->key, null, [], $t->input !== null, null);
            if ($t->when !== null) {
                $this->checkCondition($t->when, $t->id, $env->with(inputAllowed: false));
            }
            if ($t->auto !== null) {
                $auto = $t->auto;
                if ($auto->after !== null && IsoDuration::tryParse($auto->after) === null) {
                    $this->error('badDuration', $t->id, "'{$auto->after}' is not a valid time.");
                }
                if ($auto->after === null && $auto->when === null) {
                    $this->error('badAuto', $t->id, "Choose when '{$t->label}' happens automatically.");
                }
                if ($auto->when !== null) {
                    $this->checkCondition($auto->when, $t->id, $env->with(inputAllowed: false));
                }
                if ($t->input !== null) {
                    $this->warn('autoWithInput', $t->id, "'{$t->label}' asks for input but also happens automatically; automatic moves skip the form.");
                }
            }
            if ($t->input?->form !== null && ($t->input->form->pages ?? null) === null) {
                $this->error('badForm', $t->id, "The form of '{$t->label}' is not a form definition.");
            }
            if ($t->steps !== null) {
                $this->lintSteps($t->steps, $env, "transition:{$t->id}");
            }
        }
        if ($lc->reset !== null) {
            $reset = $lc->reset;
            // Problems about the reset have no node of their own; the editor shows them on the lifecycle.
            $back = $reset->to ?? array_find($lc->states, fn (State $x) => $x->isInitial())?->id;
            $final = array_find($lc->states, fn (State $s) => $s->id === $back);
            if ($reset->to !== null && ! $isState($reset->to)) {
                $this->error('unknownState', null, 'The reset goes back to a state that does not exist.');
            } elseif ($final?->isTerminal()) {
                $this->warn('resetToFinal', null, "The reset goes back to the final state '{$final->label}', which ends the workflow.");
            }
            $this->checkAssignees($reset->who, null, true);
            if (($reset->who ?? []) === []) {
                $this->warn('resetNobody', null, 'Nobody can reset this workflow yet; choose who may.');
            }
            if ($reset->states !== null) {
                foreach ($reset->states as $st) {
                    if (! $isState($st)) {
                        $this->error('unknownState', null, "The reset is offered in a state that does not exist ('{$st}').");
                    }
                }
                if ($reset->states === []) {
                    $this->warn('resetNeverOffered', null, 'The reset is offered in no state, so nobody can ever use it.');
                }
            }
            if ($reset->input?->form !== null && ($reset->input->form->pages ?? null) === null) {
                $this->error('badForm', null, "The reset's form is not a form definition.");
            }
            if ($reset->steps !== null) {
                $this->lintSteps($reset->steps, new LintEnv($entity?->key, null, [], $reset->input !== null, null), 'reset');
            }
        }
        // Reachability from the initial state.
        $initial = array_find($lc->states, fn (State $s) => $s->isInitial());
        if ($initial !== null) {
            $seen = [$initial->id => true];
            $queue = [$initial->id];
            while ($queue !== []) {
                $current = array_shift($queue);
                foreach ($lc->transitions as $t) {
                    if ($t->from->allows($lc, $current, $t->to) && ! isset($seen[$t->to])) {
                        $seen[$t->to] = true;
                        $queue[] = $t->to;
                    }
                }
            }
            foreach ($lc->states as $s) {
                if (! isset($seen[$s->id])) {
                    $this->warn('unreachableState', $s->id, "No transition leads to '{$s->label}'.");
                }
            }
        }
        foreach ($lc->states as $s) {
            if (! $s->isTerminal() && ! array_any($lc->transitions, fn (Transition $t) => $t->from->allows($lc, $s->id, $t->to))) {
                $this->warn('deadEndState', $s->id, "'{$s->label}' has no way out. Mark it as a final state or add a transition.");
            }
        }
        if ($lc->states !== [] && ! array_any($lc->states, fn (State $s) => $s->isTerminal())) {
            $this->warn('noTerminalState', null, 'No state is marked as final, so records never finish.');
        }
    }

    // ── Steps ─────────────────────────────────────────────────────────

    /** @param  list<Step>  $steps */
    private function lintSteps(array $steps, LintEnv $env, string $listId): void
    {
        $upstream = $env->upstream;
        $lists = [...$env->enclosingLists, $listId];
        foreach ($steps as $i => $s) {
            $this->checkId($s->id, $s->id);
            $this->allSteps[$s->id] ??= $s;
            $here = $env->with(upstream: $upstream, enclosingLists: $lists);
            $this->lintStep($s, $here, $listId, $i);
            if ($s instanceof NotifyStep && ($s->retry !== null || $s->onError !== null)) {
                $this->warn('notifyDelivery', $s->id, 'Retry and on-error here only cover working out the recipients and the message; delivery happens after the step through the outbox, which retries on its own.');
            }
            if ($s->retry !== null) {
                $r = $s->retry;
                if ($r->max < 0 || $r->max > 10) {
                    $this->error('badRetry', $s->id, 'Retries can be 0 to 10.');
                }
                if ($r->initial !== null && IsoDuration::tryParse($r->initial) === null) {
                    $this->error('badDuration', $s->id, "'{$r->initial}' is not a valid delay.");
                }
                if ($r->maxDelay !== null && IsoDuration::tryParse($r->maxDelay) === null) {
                    $this->error('badDuration', $s->id, "'{$r->maxDelay}' is not a valid delay.");
                }
            }
            if ($s->timeout !== null && IsoDuration::tryParse($s->timeout) === null) {
                $this->error('badDuration', $s->id, "'{$s->timeout}' is not a valid time limit.");
            }
            if ($s->onError?->mode === 'steps' && $s->onError->steps !== null) {
                $this->lintSteps($s->onError->steps, $here->with(upstream: [...$upstream, $s]), "{$s->id}/error");
            }
            $upstream[] = $s;
        }
    }

    private function lintStep(Step $s, LintEnv $env, string $listId, int $index): void
    {
        match (true) {
            $s instanceof UnknownStep => $this->error('unknownStepType', $s->id, "'{$s->type}' is not a step this engine knows."),
            $s instanceof BranchStep => $this->lintBranch($s, $env),
            $s instanceof SwitchStep => $this->lintSwitch($s, $env),
            $s instanceof ParallelStep => $this->lintParallel($s, $env),
            $s instanceof ForEachStep => $this->lintForEach($s, $env),
            $s instanceof ApprovalStep => $this->lintApproval($s, $env),
            $s instanceof TaskStep => $this->lintTask($s, $env),
            $s instanceof WaitStep => $this->lintWait($s, $env),
            $s instanceof SetFieldsStep => $this->lintSetFields($s, $env),
            $s instanceof CreateRecordStep => $this->lintCreateRecord($s, $env),
            $s instanceof SetVariablesStep => $this->lintSetVariables($s, $env),
            $s instanceof ApplyTransitionStep => $this->lintApplyTransition($s, $env),
            $s instanceof NotifyStep => $this->lintNotify($s, $env),
            $s instanceof WebhookStep => $this->lintWebhook($s, $env),
            $s instanceof ActionStep => $this->lintAction($s, $env),
            $s instanceof SubWorkflowStep => $this->lintSubWorkflow($s, $env),
            $s instanceof GoToStep => $this->lintGoTo($s, $env, $listId, $index),
            $s instanceof EndStep => $this->checkTemplate($s->reason, $s->id, $env),
            default => null,
        };
    }

    private function lintBranch(BranchStep $b, LintEnv $env): void
    {
        if ($b->paths === []) {
            $this->error('emptyBranch', $b->id, "'".self::name($b)."' has no paths.");
        }
        foreach ($b->paths as $p) {
            $this->checkId($p->id, $b->id);
            if ($p->when === null) {
                $this->error('missingCondition', $b->id, "Path '{$p->label}' of '".self::name($b)."' needs a condition.");
            } else {
                $this->checkCondition($p->when, $b->id, $env);
            }
            $this->lintSteps($p->steps, $env->inside($b), "{$b->id}/path:{$p->id}");
        }
        if ($b->otherwise !== null) {
            $this->checkId($b->otherwise->id, $b->id);
            $this->lintSteps($b->otherwise->steps, $env->inside($b), "{$b->id}/otherwise");
        }
    }

    private function lintSwitch(SwitchStep $sw, LintEnv $env): void
    {
        $this->checkToken($sw->on, $sw->id, $env);
        if ($sw->cases === []) {
            $this->error('emptySwitch', $sw->id, "'".self::name($sw)."' has no cases.");
        }
        foreach ($sw->cases as $c) {
            $this->checkId($c->id, $sw->id);
            if ($c->values === []) {
                $this->error('missingValues', $sw->id, "Case '{$c->label}' of '".self::name($sw)."' needs at least one value.");
            }
            $this->lintSteps($c->steps, $env->inside($sw), "{$sw->id}/case:{$c->id}");
        }
        if ($sw->otherwise !== null) {
            $this->checkId($sw->otherwise->id, $sw->id);
            $this->lintSteps($sw->otherwise->steps, $env->inside($sw), "{$sw->id}/otherwise");
        }
    }

    private function lintParallel(ParallelStep $par, LintEnv $env): void
    {
        if (count($par->paths) < 2) {
            $this->error('parallelPaths', $par->id, "'".self::name($par)."' needs at least two paths.");
        }
        $count = $par->join->count;
        if ($par->join->type === 'count' && ($count === null || $count < 1 || $count > count($par->paths))) {
            $this->error('parallelJoinCount', $par->id, "'".self::name($par)."' waits for {$count} paths but has ".count($par->paths).'.');
        }
        foreach ($par->paths as $p) {
            $this->checkId($p->id, $par->id);
            $this->lintSteps($p->steps, $env->inside($par)->with(inParallelOrLoop: true, enclosingLists: []), "{$par->id}/path:{$p->id}");
        }
    }

    private function lintForEach(ForEachStep $fe, LintEnv $env): void
    {
        $collectionEntity = $this->collectionEntity($fe->collection, $env, $fe->id);
        if ($fe->max < 1 || $fe->max > 500) {
            $this->error('badLimit', $fe->id, 'For each can cover 1 to 500 items.');
        }
        $loopEnv = $env->inside($fe)->with(itemEntity: $collectionEntity, inParallelOrLoop: true, enclosingLists: []);
        if ($fe->when !== null) {
            $this->checkCondition($fe->when, $fe->id, $loopEnv);
        }
        $this->lintSteps($fe->steps, $loopEnv, "{$fe->id}/each");
    }

    private function lintApproval(ApprovalStep $ap, LintEnv $env): void
    {
        $this->lintHuman($ap, $env);
        $role = $ap->makerChecker?->requiredRole;
        if ($role !== null && $this->r->roles !== [] && ! $this->hasKey($this->r->roles, $role)) {
            $this->error('unknownRole', $ap->id, "'{$role}' is not a role the app has.");
        }
        if (in_array($ap->rule->type, ['count', 'percent'], true) && ($ap->rule->value === null || $ap->rule->value < 1)) {
            $this->error('badRule', $ap->id, "'".self::name($ap)."' needs a number of approvals.");
        }
        if ($ap->rule->type === 'percent' && $ap->rule->value !== null && $ap->rule->value > 100) {
            $this->error('badRule', $ap->id, 'A percentage cannot be over 100.');
        }
        if ($ap->outcomes->timedOut === []) {
            $this->warn('emptyTimeoutPath', $ap->id, "Nothing happens when '".self::name($ap)."' runs out of time; the workflow just carries on.");
        }
        $this->lintSteps($ap->outcomes->approved, $env->inside($ap), "{$ap->id}/outcome:approved");
        $this->lintSteps($ap->outcomes->rejected, $env->inside($ap), "{$ap->id}/outcome:rejected");
        $this->lintSteps($ap->outcomes->timedOut, $env->inside($ap), "{$ap->id}/outcome:timedOut");
    }

    private function lintTask(TaskStep $tk, LintEnv $env): void
    {
        $this->lintHuman($tk, $env);
        if ($tk->outcomes === []) {
            $this->error('noOutcomes', $tk->id, "'".self::name($tk)."' needs at least one button (outcome).");
        }
        foreach ($tk->outcomes as $o) {
            $this->checkId($o->id, $tk->id);
            if (count(array_filter($tk->outcomes, fn (TaskOutcome $x) => $x->key === $o->key)) > 1) {
                $this->error('duplicateKey', $tk->id, "Two outcomes of '".self::name($tk)."' use the key '{$o->key}'.");
            }
            $this->lintSteps($o->steps, $env->inside($tk), "{$tk->id}/outcome:{$o->key}");
        }
        if ($tk->requireComment === 'reject' && ! array_any($tk->outcomes, fn (TaskOutcome $o) => $o->tone === 'danger' || $o->key === 'rejected')) {
            $this->warn('noNegativeOutcome', $tk->id, "'".self::name($tk)."' asks for a comment on rejecting, but none of its buttons is red (danger), so it never does.");
        }
        $this->checkId($tk->onTimeout->id, $tk->id);
        $this->lintSteps($tk->onTimeout->steps, $env->inside($tk), "{$tk->id}/onTimeout");
    }

    private function lintSetFields(SetFieldsStep $sf, LintEnv $env): void
    {
        $def = $this->def;
        $target = $this->targetEntity($sf->target, $env, $sf->id);
        if ($sf->values === []) {
            $this->error('noValues', $sf->id, "'".self::name($sf)."' does not change anything yet.");
        }
        foreach ($sf->values as $v) {
            $f = $this->r->field($target, $v->field);
            if ($target !== null && $f === null) {
                $this->error('unknownField', $sf->id, "'{$v->field}' is not a field of {$this->r->entity($target)?->label}.");
            } elseif ($f !== null && ! $f->isSettable()) {
                $this->error('fieldNotSettable', $sf->id, "Workflows cannot change {$f->label}; the app has not allowed it.");
            } elseif ($f !== null && $this->notAnOption($f, $v->value)) {
                $this->error('unknownValue', $sf->id, "'".Values::text($v->value->const)."' is not one of {$f->label}'s values.");
            }
            if ($f !== null && $def->isLifecycle() && $def->lifecycle?->field === $f->key && $target === $def->entity) {
                $this->error('ownedField', $sf->id, "{$f->label} is controlled by this lifecycle; use 'Move lifecycle' instead.");
            }
            $owner = array_find($this->ctx->otherPublished, fn (WorkflowDefinition $d) => $d->isLifecycle() && $d->entity === $target
                && $d->lifecycle?->field === $v->field && $d->lifecycle->enforce());
            if ($owner !== null) {
                $this->error('ownedField', $sf->id, ($f?->label ?? $v->field)." is controlled by the lifecycle '{$owner->title}'; use 'Move lifecycle' instead.");
            }
            $this->checkOperand($v->value, $sf->id, $env);
        }
    }

    private function lintCreateRecord(CreateRecordStep $cr, LintEnv $env): void
    {
        $def = $this->def;
        $target = $this->r->entity($cr->entity);
        if ($target === null) {
            $this->error($cr->entity === '' ? 'missingEntity' : 'unknownEntity', $cr->id, "Choose which kind of record '".self::name($cr)."' creates.");

            return;
        }
        // Fields a lifecycle controls (this one or a published one): a new record starts in the lifecycle's first state.
        $owned = fn (string $field) => ($def->isLifecycle() && $def->entity === $target->key && $def->lifecycle?->field === $field)
            || array_any($this->ctx->otherPublished, fn (WorkflowDefinition $d) => $d->isLifecycle() && $d->entity === $target->key && $d->lifecycle?->field === $field);
        foreach ($cr->values as $v) {
            $f = $target->field($v->field);
            if ($f === null) {
                $this->error('unknownField', $cr->id, "'{$v->field}' is not a field of {$target->label}.");
            } elseif (! $f->isSettable()) {
                $this->error('fieldNotSettable', $cr->id, "Workflows cannot set {$f->label}; the app has not allowed it.");
            } elseif ($owned($f->key)) {
                $this->error('ownedField', $cr->id, "{$f->label} is controlled by a lifecycle; a new record starts in its first state.");
            } elseif ($this->notAnOption($f, $v->value)) {
                $this->error('unknownValue', $cr->id, "'".Values::text($v->value->const)."' is not one of {$f->label}'s values.");
            }
            $this->checkOperand($v->value, $cr->id, $env);
        }
        // Fields the app requires must be given (a lifecycle's field is filled with its first state).
        foreach ($target->fields as $f) {
            if ($f->required === true && $f->type !== FieldTypes::COLLECTION && ! $owned($f->key) && ! array_any($cr->values, fn ($v) => $v->field === $f->key)) {
                $this->error('missingRequiredField', $cr->id, "'".self::name($cr)."' must set {$f->label}; the app requires it.");
            }
        }
    }

    private function lintSetVariables(SetVariablesStep $sv, LintEnv $env): void
    {
        foreach ($sv->values as $v) {
            if (! array_any($this->def->variables ?? [], fn (Variable $x) => $x->id === $v->variable)) {
                $this->error('unknownVariable', $sv->id, "'".self::name($sv)."' sets a variable that does not exist.");
            }
            $this->checkOperand($v->value, $sv->id, $env);
        }
    }

    private function lintApplyTransition(ApplyTransitionStep $at, LintEnv $env): void
    {
        $def = $this->def;
        $atEntity = $this->targetEntity($at->target, $env, $at->id);
        $lcDef = $def->isLifecycle() && $def->key === $at->lifecycle
            ? $def
            : array_find($this->ctx->otherPublished, fn (WorkflowDefinition $d) => $d->isLifecycle() && $d->key === $at->lifecycle);
        if ($lcDef === null) {
            $this->error('unknownLifecycle', $at->id, "There is no published lifecycle called '{$at->lifecycle}'.");
        } else {
            if ($lcDef->entity !== $atEntity) {
                $this->error('lifecycleEntity', $at->id, "The lifecycle '{$lcDef->title}' is for a different record type.");
            }
            if (! array_any($lcDef->lifecycle?->transitions ?? [], fn (Transition $t) => $t->key === $at->transition)) {
                $this->error('unknownTransition', $at->id, "'{$at->transition}' is not a transition of {$lcDef->title}.");
            }
        }
        foreach ($at->input ?? [] as $v) {
            $this->checkOperand($v, $at->id, $env);
        }
        $this->checkTemplate($at->comment, $at->id, $env);
    }

    private function lintNotify(NotifyStep $n, LintEnv $env): void
    {
        if (! $this->hasKey($this->r->channels, $n->channel)) {
            $this->error('unknownChannel', $n->id, "'{$n->channel}' is not a notification channel the app offers.");
        } elseif (($this->ctx->settings->channels[$n->channel] ?? null)?->enabled === false) {
            $this->warn('channelDisabled', $n->id, "The {$n->channel} channel is switched off in settings.");
        }
        if ($n->to === []) {
            $this->error('noRecipients', $n->id, "'".self::name($n)."' has nobody to send to.");
        }
        $this->checkAssignees($n->to, $n->id, true, $env);
        $this->checkTemplate($n->subject, $n->id, $env);
        $this->checkTemplate($n->body, $n->id, $env);
        if ($n->body->parts === []) {
            $this->error('emptyMessage', $n->id, "'".self::name($n)."' has no message.");
        }
    }

    private function lintAction(ActionStep $act, LintEnv $env): void
    {
        $desc = $this->r->action($act->action);
        if ($desc === null) {
            $this->error('unknownAction', $act->id, "'{$act->action}' is not an action the app offers.");
        } else {
            if ($desc->entity !== null && $desc->entity !== $env->entity) {
                $this->error('actionEntity', $act->id, "'{$desc->label}' works on a different record type.");
            }
            foreach ($desc->inputs as $i) {
                if ($i->required === true && ! array_key_exists($i->key, $act->input ?? [])) {
                    $this->error('missingInput', $act->id, "'".self::name($act)."' needs a value for {$i->label}.");
                }
            }
            foreach (array_keys($act->input ?? []) as $k) {
                if (! array_any($desc->inputs, fn ($i) => $i->key === (string) $k)) {
                    $this->warn('unknownInput', $act->id, "'{$desc->label}' has no input called '{$k}'.");
                }
            }
        }
        foreach ($act->input ?? [] as $v) {
            $this->checkOperand($v, $act->id, $env);
        }
    }

    private function lintSubWorkflow(SubWorkflowStep $sub, LintEnv $env): void
    {
        if ($sub->workflow === $this->def->key) {
            $this->error('selfSubWorkflow', $sub->id, 'A workflow cannot run itself.');
        } else {
            $child = array_find($this->ctx->otherPublished, fn (WorkflowDefinition $d) => $d->key === $sub->workflow);
            if ($child === null) {
                $this->error('unknownWorkflow', $sub->id, "There is no published workflow called '{$sub->workflow}'.");
            } elseif ($child->isLifecycle()) {
                $this->error('notAutomation', $sub->id, "'{$child->title}' is a lifecycle; only automations can be run as steps.");
            } elseif ($child->entity !== null && $child->entity !== $this->targetEntity($sub->target, $env, $sub->id)) {
                $this->error('workflowEntity', $sub->id, "'{$child->title}' is about a different record type.");
            } elseif (($path = $this->cycleThrough($sub->workflow)) !== null) {
                $this->error('subWorkflowCycle', $sub->id, "'".self::name($sub)."' runs '{$child->title}', which leads back to this workflow (".implode(' → ', $path)."). Workflows that run each other never stop; use 'Go back' for repeats.");
            }
        }
        foreach ($sub->input ?? [] as $v) {
            $this->checkOperand($v, $sub->id, $env);
        }
    }

    /**
     * The chain of workflow keys from `$start` back to this definition through sub-workflow steps of the
     * published workflows (this definition in place of its own published version), or null when there is none.
     *
     * @return list<string>|null
     */
    private function cycleThrough(string $start): ?array
    {
        $calls = fn (CompiledDefinition $c) => array_values(array_unique(array_map(
            fn (SubWorkflowStep $s) => $s->workflow,
            array_filter(array_merge([], ...array_values($c->lists)), fn (Step $s) => $s instanceof SubWorkflowStep),
        )));
        if ($this->subGraph === null) {
            $this->subGraph = [];
            foreach ($this->ctx->otherPublished as $d) {
                if ($d->id !== $this->def->id && ! $d->isLifecycle()) {
                    $this->subGraph[$d->key] = $calls(new CompiledDefinition($d, $d->id, 0));
                }
            }
            $this->subGraph[$this->def->key] = $calls($this->compiled);
        }
        $seen = [];
        $walk = function (string $key, array $trail) use (&$walk, &$seen): ?array {
            if ($key === $this->def->key) {
                return [$this->def->key, ...$trail];
            }
            if (isset($seen[$key])) {
                return null;
            }
            $seen[$key] = true;
            if (! array_key_exists($key, $this->subGraph)) {
                return null;
            }
            foreach ($this->subGraph[$key] as $next) {
                if (($found = $walk($next, [...$trail, $next])) !== null) {
                    return $found;
                }
            }

            return null;
        };

        return $walk($start, [$start]);
    }

    private function lintGoTo(GoToStep $g, LintEnv $env, string $listId, int $index): void
    {
        if ($g->maxTimes < 1 || $g->maxTimes > 50) {
            $this->error('badLimit', $g->id, 'Go back can repeat 1 to 50 times.');
        }
        // Valid targets come before the current position, in this list or an enclosing one.
        $reachable = array_any($env->enclosingLists, fn (string $list) => ($idx = $this->findIndex($list, $g->target)) !== null && ($list !== $listId || $idx < $index));
        if (! $reachable) {
            $this->error('goToInvalidTarget', $g->id, "'".self::name($g)."' must go back to an earlier step on the same path.");
        }
    }

    private function findIndex(string $listId, string $stepId): ?int
    {
        $list = $this->compiled->lists[$listId] ?? null;
        if ($list === null) {
            return null;
        }
        $index = array_find_key($list, fn (Step $x) => $x->id === $stepId);

        return $index === null ? null : (int) $index;
    }

    private function lintHuman(HumanStep $h, LintEnv $env): void
    {
        $this->checkAssignees($h->assignees, $h->id, false, $env);
        $due = IsoDuration::tryParse($h->due);
        if ($due === null || $due->negative || $due->isZero()) {
            $this->error('badDuration', $h->id, "'".self::name($h)."' needs a due time.");
        }
        // A due time that does not parse counts as zero here, as in .NET (every reminder is then late).
        $dueSeconds = $due?->approximateSeconds() ?? 0;
        foreach ($h->reminders ?? [] as $r) {
            $rd = IsoDuration::tryParse($r);
            if ($rd === null) {
                $this->error('badDuration', $h->id, "'{$r}' is not a valid reminder time.");
            } elseif ($rd->approximateSeconds() >= $dueSeconds) {
                $this->warn('lateReminder', $h->id, "A reminder of '".self::name($h)."' is set after it is due.");
            }
        }
        if ($h->escalation !== null) {
            if (IsoDuration::tryParse($h->escalation->after) === null) {
                $this->error('badDuration', $h->id, "'{$h->escalation->after}' is not a valid escalation time.");
            }
            $this->checkAssignees($h->escalation->assignees, $h->id, false, $env);
        }
        $this->checkTemplate($h->instructions, $h->id, $env);
        if ($h->form !== null && ($h->form->pages ?? null) === null) {
            $this->error('badForm', $h->id, "The form of '".self::name($h)."' is not a form definition.");
        }
    }

    private function lintWait(WaitStep $w, LintEnv $env): void
    {
        $u = $w->until;
        switch ($u->type) {
            case 'duration':
                if (IsoDuration::tryParse($u->duration)?->negative ?? true) {
                    $this->error('badDuration', $w->id, "'".self::name($w)."' needs a length of time.");
                }
                break;
            case 'date':
                if ($u->date === null) {
                    $this->error('missingDate', $w->id, "'".self::name($w)."' needs a date.");
                } else {
                    $this->checkOperand($u->date, $w->id, $env);
                }
                if ($u->offset !== null && IsoDuration::tryParse($u->offset) === null) {
                    $this->error('badDuration', $w->id, "'{$u->offset}' is not a valid offset.");
                }
                break;
            case 'condition':
                if ($u->when === null) {
                    $this->error('missingCondition', $w->id, "'".self::name($w)."' needs a condition.");
                } else {
                    $this->checkCondition($u->when, $w->id, $env);
                }
                if (IsoDuration::tryParse($u->timeout) === null) {
                    $this->error('badDuration', $w->id, "'".self::name($w)."' needs a time limit.");
                }
                if ($env->entity === null) {
                    $this->error('missingEntity', $w->id, "'".self::name($w)."' waits on the record, but this workflow has no record.");
                }
                break;
            case 'signal':
                if ($this->r->signal($u->signal) === null) {
                    $this->error('unknownSignal', $w->id, "'{$u->signal}' is not an event the app sends.");
                }
                if ($u->timeout !== null && IsoDuration::tryParse($u->timeout) === null) {
                    $this->error('badDuration', $w->id, "'{$u->timeout}' is not a valid time limit.");
                }
                break;
            default:
                $this->error('badWait', $w->id, "'".self::name($w)."' has an unknown kind of wait.");
                break;
        }
        if ($u->onTimeout !== null) {
            $this->checkId($u->onTimeout->id, $w->id);
            $this->lintSteps($u->onTimeout->steps, $env->inside($w), "{$w->id}/onTimeout");
        }
    }

    private function lintWebhook(WebhookStep $wh, LintEnv $env): void
    {
        $webhooks = $this->ctx->settings->webhooks;
        if (! in_array($wh->method, WebhookStep::METHODS, true)) {
            $this->error('badMethod', $wh->id, "'{$wh->method}' is not an HTTP method a webhook can use (".implode(', ', WebhookStep::METHODS).', in capitals).');
        }
        if ($wh->connection !== null) {
            if (! array_any($webhooks?->connections ?? [], fn (WebhookConnection $c) => $c->key === $wh->connection)) {
                $this->error('unknownConnection', $wh->id, "There is no connection called '{$wh->connection}' in settings.");
            }
        } elseif (self::blank($wh->url) || ! (($uri = AbsoluteUrl::tryParse($wh->url))?->isHttp() ?? false)) {
            $this->error('badUrl', $wh->id, "'".self::name($wh)."' needs a web address (or a connection from settings).");
        } else {
            $origin = $uri->origin();
            if (! array_any($webhooks?->allowedOrigins ?? [], fn (string $o) => strcasecmp($o, $origin) === 0)) {
                $this->error('webhookOriginNotAllowed', $wh->id, "{$origin} is not in the allowed addresses in settings.");
            }
        }
        $this->checkTemplate($wh->path, $wh->id, $env);
        foreach ($wh->headers ?? [] as $header) {
            $this->checkTemplate($header, $wh->id, $env);
        }
        foreach (self::bodyRefs($wh->body) as $ref) {
            $this->checkToken($ref, $wh->id, $env);
        }
    }

    /** @return list<string> the token paths of `{"ref": path}` objects in a webhook body */
    public static function bodyRefs(mixed $node): array
    {
        if ($node instanceof stdClass) {
            $members = get_object_vars($node);
            if (count($members) === 1 && is_string($members['ref'] ?? null)) {
                return [$members['ref']];
            }

            return array_merge([], ...array_map(self::bodyRefs(...), array_values($members)));
        }

        return is_array($node) ? array_merge([], ...array_map(self::bodyRefs(...), $node)) : [];
    }

    // ── Shared checks ─────────────────────────────────────────────────

    /** @param  list<Assignee>|null  $list */
    private function checkAssignees(?array $list, ?string $node, bool $allowEmpty, ?LintEnv $env = null): void
    {
        if ($list === null || $list === []) {
            if (! $allowEmpty) {
                $this->error('noAssignees', $node, 'Choose who this is for.');
            }

            return;
        }
        $r = $this->r;
        foreach ($list as $a) {
            if ($a->role !== null && $r->roles !== [] && ! $this->hasKey($r->roles, $a->role)) {
                $this->error('unknownRole', $node, "'{$a->role}' is not a role the app has.");
            }
            if ($a->group !== null && $r->groups !== [] && ! $this->hasKey($r->groups, $a->group)) {
                $this->error('unknownGroup', $node, "'{$a->group}' is not a group the app has.");
            }
            if ($a->department !== null && ($r->departments ?? []) !== [] && ! $this->hasKey($r->departments, $a->department)) {
                $this->error('unknownDepartment', $node, "'{$a->department}' is not a department the app has.");
            }
            if ($a->portal !== null && ($r->portals ?? []) !== [] && ! $this->hasKey($r->portals, $a->portal)) {
                $this->error('unknownPortal', $node, "'{$a->portal}' is not a portal the app has.");
            }
            if ($a->queue !== null && ($r->queues ?? []) !== [] && ! $this->hasKey($r->queues, $a->queue)) {
                $this->error('unknownQueue', $node, "'{$a->queue}' is not a queue the app has.");
            }
            if ($a->field !== null) {
                if ($env !== null) {
                    $this->checkToken($a->field, $node, $env, true);
                } elseif (! str_starts_with($a->field, 'entity.')) {
                    $this->error('unknownToken', $node, "'{$a->field}' is not a person on the record.");
                } else {
                    // Transitions, the reset and manual starts name people on the record: the field must exist and hold a person.
                    $this->checkToken($a->field, $node, new LintEnv($this->def->entity, null, [], false, null), true);
                }
            }
            if ($a->managerOf !== null && $a->managerOf !== 'initiator' && $a->managerOf !== 'assignees' && $env !== null) {
                $this->checkToken($a->managerOf, $node, $env, true);
            }
            if ($a->email !== null) {
                $this->checkTemplate($a->email, $node, $env ?? new LintEnv($this->def->entity, null, [], true, $this->def->trigger));
            }
        }
    }

    /**
     * Whether two "applies to" conditions can never hold for the same record: both require the same field
     * to equal constants that "is" (`eq`) treats as different (the common "Type is Passport" / "Type is Visa" case).
     */
    private static function clearlyApart(Condition $a, Condition $b): bool
    {
        $required = function (Condition $c): array {
            $rules = match (true) {
                $c instanceof ConditionRule => [$c],
                $c instanceof ConditionGroup && $c->match === 'all' => array_filter($c->conditions, fn ($x) => $x instanceof ConditionRule),
                default => [],
            };
            $out = [];
            foreach ($rules as $rule) {
                if ($rule->op === 'eq' && $rule->value?->hasConst && ! array_key_exists($rule->ref, $out)) {
                    $out[$rule->ref] = $rule->value->const;
                }
            }

            return $out;
        };
        $ra = $required($a);
        // Compared the way "is" compares (case, spaces, numbers and yes/no alike), so "passport" and "Passport" are not apart.
        foreach ($required($b) as $ref => $value) {
            if (array_key_exists($ref, $ra) && ! Conditions::same($ra[$ref], $value)) {
                return true;
            }
        }

        return false;
    }

    private function checkCondition(Condition $c, ?string $node, LintEnv $env): void
    {
        if (! $c->isComplete()) {
            $this->error('incompleteCondition', $node, 'A condition is not finished.');
        }
        foreach ($c->refs() as $ref) {
            $this->checkToken($ref, $node, $env);
        }
        $this->checkOps($c, $node);
    }

    private function checkOps(Condition $c, ?string $node): void
    {
        if ($c instanceof ConditionGroup) {
            if (! in_array($c->match, ['all', 'any', 'none'], true)) {
                $this->error('badCondition', $node, "'{$c->match}' is not a way to combine conditions.");
            }
            foreach ($c->conditions as $x) {
                $this->checkOps($x, $node);
            }
        } elseif ($c instanceof ConditionRule && in_array($c->op, ['visible', 'hidden', 'complete', 'incomplete', 'rowsGte', 'rowsLte'], true)) {
            $this->error('formOnlyOperator', $node, "'{$c->op}' only works in forms.");
        }
    }

    private function checkOperand(Operand $o, ?string $node, LintEnv $env): void
    {
        if ($o->ref !== null) {
            $this->checkToken($o->ref, $node, $env);
        }
        foreach ($o->calc?->refs() ?? [] as $ref) {
            $this->checkToken($ref, $node, $env);
        }
    }

    private function checkTemplate(?Template $t, ?string $node, LintEnv $env): void
    {
        foreach ($t?->refs() ?? [] as $ref) {
            $this->checkToken($ref, $node, $env);
        }
    }

    private function targetEntity(?string $target, LintEnv $env, string $node): ?string
    {
        if ($target === null || $target === 'self') {
            return $env->entity;
        }
        if ($target === 'item') {
            if ($env->itemEntity === null) {
                $this->error('badTarget', $node, "'item' can only be changed inside 'For each'.");
            }

            return $env->itemEntity;
        }
        $f = $this->fieldAt($target, $env);
        if ($f === null || $f->type !== FieldTypes::RELATION) {
            $this->error('badTarget', $node, "'{$target}' is not a related record.");

            return null;
        }

        return $f->entity;
    }

    private function collectionEntity(string $path, LintEnv $env, string $node): ?string
    {
        $f = $this->fieldAt($path, $env);
        if ($f === null || $f->type !== FieldTypes::COLLECTION) {
            $this->error('notACollection', $node, "'{$path}' is not a list of related records.");

            return null;
        }

        return $f->entity;
    }

    /** The registered field an `entity.*`/`item.*` path ends at. */
    private function fieldAt(string $path, LintEnv $env): ?FieldDescriptor
    {
        $parts = explode('.', $path);
        $entity = match ($parts[0]) {
            'entity' => $env->entity,
            'item' => $env->itemEntity,
            default => null,
        };
        $f = null;
        for ($i = 1; $i < count($parts) && $entity !== null; $i++) {
            $f = $this->r->field($entity, $parts[$i]);
            if ($f === null || ! $f->isReadable()) {
                return null;
            }
            $entity = $f->type === FieldTypes::RELATION || $f->type === FieldTypes::COLLECTION ? $f->entity : null;
            if ($entity === null && $i < count($parts) - 1) {
                return $f->type === FieldTypes::JSON ? $f : null;
            }
        }

        return $f;
    }

    private function checkToken(string $path, ?string $node, LintEnv $env, bool $expectUser = false): void
    {
        $parts = explode('.', $path);
        switch ($parts[0]) {
            case 'entity':
            case 'item':
                if ($parts[0] === 'entity' && $env->entity === null) {
                    $this->error('unknownToken', $node, "'{$path}' reads the record, but this workflow has no record.");

                    return;
                }
                if ($parts[0] === 'item' && $env->itemEntity === null) {
                    $this->error('unknownToken', $node, "'{$path}' can only be used inside 'For each'.");

                    return;
                }
                if (count($parts) === 2 && ($parts[1] === 'id' || $parts[1] === 'index')) {
                    return;
                }
                $f = $this->fieldAt($path, $env);
                if ($f === null) {
                    $this->error('unknownToken', $node, "'{$path}' is not a field the app allows workflows to read.");
                } elseif ($expectUser && $f->type !== FieldTypes::USER) {
                    $this->error('notAUser', $node, "{$f->label} does not hold a person.");
                }

                return;
            case 'trigger':
                if (count($parts) < 2 || ! in_array($parts[1], ['field', 'from', 'to', 'actor', 'actorName', 'at', 'transition', 'data', 'type', 'comment'], true)) {
                    $this->error('unknownToken', $node, "'{$path}' is not something the trigger provides.");
                }
                if ($expectUser && ($parts[1] ?? null) !== 'actor') {
                    $this->error('notAUser', $node, "'{$path}' does not hold a person.");
                }

                return;
            case 'input':
                if (! $env->inputAllowed) {
                    $this->error('unknownToken', $node, "'{$path}' reads a form, but there is no form here.");
                }

                return;
            case 'steps':
                if (count($parts) < 3) {
                    $this->error('unknownToken', $node, "'{$path}' is incomplete.");

                    return;
                }
                $up = array_find($env->upstream, fn (Step $s) => $s->id === $parts[1]);
                if ($up === null) {
                    $this->error(isset($this->compiled->steps[$parts[1]]) ? 'tokenNotUpstream' : 'unknownToken', $node, "'{$path}' reads a step that may not have run yet at this point.");

                    return;
                }
                $action = $up instanceof ActionStep ? $this->r->action($up->action) : null;
                $outputs = $action !== null
                    ? [...array_map(fn ($o) => $o->key, $action->outputs), 'error']
                    : (Linter::STEP_OUTPUTS[$up->type] ?? []);
                if (! in_array($parts[2], $outputs, true)) {
                    $this->error('unknownToken', $node, "'".self::name($up)."' has no output called '{$parts[2]}'.");
                }

                return;
            case 'vars':
                if (count($parts) < 2 || ! array_any($this->def->variables ?? [], fn (Variable $v) => $v->key === $parts[1])) {
                    $this->error('unknownToken', $node, "'{$path}' is not a variable of this workflow.");
                }

                return;
            case 'instance':
                if (count($parts) !== 2 || ! in_array($parts[1], ['id', 'startedAt', 'initiator'], true)) {
                    $this->error('unknownToken', $node, "'{$path}' is not something the instance provides.");
                }

                return;
            case 'now':
            case 'today':
                if (count($parts) !== 1) {
                    $this->error('unknownToken', $node, "'{$path}' is not valid.");
                }

                return;
            default:
                $this->error('unknownToken', $node, "'{$path}' is not a value workflows can read.");
        }
    }

    /** @param  list<FieldOption>  $options */
    private function isOption(array $options, string $text): bool
    {
        return array_any($options, fn (FieldOption $o) => Values::text($o->value) === $text);
    }

    /** A constant that is not one of an enum field's options. */
    private function notAnOption(FieldDescriptor $f, Operand $value): bool
    {
        return $value->hasConst && $f->options !== null && $value->const !== null && ! $this->isOption($f->options, Values::text($value->const));
    }

    /** @param  list<LabelledItem>  $items */
    private function hasKey(array $items, string $key): bool
    {
        return array_any($items, fn (LabelledItem $i) => $i->key === $key);
    }
}
