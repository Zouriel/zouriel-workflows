<?php

namespace Zouriel\Workflows\Core\Engine;

use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\Lifecycle;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Model\Step;
use Zouriel\Workflows\Core\Model\Transition;
use Zouriel\Workflows\Core\Model\Variable;
use Zouriel\Workflows\Core\Model\WorkflowDefinition;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * A parsed definition with every step list indexed. List ids: `root` for an automation's steps;
 * `transition:ID`, `onEnter:ID`, `onExit:ID`, `sla:ID`, `reset` for lifecycle runs; `STEPID/SLOT` for
 * lists nested in a step (e.g. `s3/path:p1`, `s5/outcome:approved`, `s2/error`). Port of .NET
 * `Engine/CompiledDefinition.cs`.
 */
final class CompiledDefinition
{
    /** @var array<string, list<Step>> step lists by list id, in the order they are found */
    public private(set) array $lists = [];

    /** @var array<string, StepLocation> every step by id (the first one wins when ids repeat; lint reports that) */
    public private(set) array $steps = [];

    public function __construct(public readonly WorkflowDefinition $definition, public readonly string $definitionId, public readonly int $version)
    {
        if ($definition->steps !== null) {
            $this->addList('root', $definition->steps, null);
        }
        if ($definition->lifecycle !== null) {
            foreach ($definition->lifecycle->states as $state) {
                if ($state->onEnter !== null) {
                    $this->addList("onEnter:{$state->id}", $state->onEnter, null);
                }
                if ($state->onExit !== null) {
                    $this->addList("onExit:{$state->id}", $state->onExit, null);
                }
                if ($state->sla !== null) {
                    $this->addList("sla:{$state->id}", $state->sla->steps, null);
                }
            }
            foreach ($definition->lifecycle->transitions as $transition) {
                if ($transition->steps !== null) {
                    $this->addList("transition:{$transition->id}", $transition->steps, null);
                }
            }
            if ($definition->lifecycle->reset?->steps !== null) {
                $this->addList('reset', $definition->lifecycle->reset->steps, null);
            }
        }
    }

    /** @param  list<Step>  $steps */
    private function addList(string $listId, array $steps, ?string $parent): void
    {
        $this->lists[$listId] = $steps;
        foreach ($steps as $index => $step) {
            $this->steps[$step->id] ??= new StepLocation($step, $listId, $index, $parent);
            foreach ($step->children() as [$slot, $children]) {
                $this->addList("{$step->id}/{$slot}", $children, $step->id);
            }
            if ($step->onError?->mode === 'steps' && $step->onError->steps !== null) {
                $this->addList("{$step->id}/error", $step->onError->steps, $step->id);
            }
        }
    }

    /** @return list<Step> */
    public function list(string $listId): array
    {
        return $this->lists[$listId] ?? throw new WorkflowException('brokenDefinition', "Step list '{$listId}' does not exist in this version.");
    }

    public function step(string $id): ?Step
    {
        return ($this->steps[$id] ?? null)?->step;
    }

    public function lifecycle(): Lifecycle
    {
        return $this->definition->lifecycle ?? throw new WorkflowException('notLifecycle', "'{$this->definition->key}' is not a lifecycle.");
    }

    public function stateById(?string $id): ?State
    {
        return array_find($this->definition->lifecycle?->states ?? [], fn (State $s) => $s->id === $id);
    }

    public function stateByKey(mixed $value): ?State
    {
        if ($this->definition->lifecycle === null || $value === null) {
            return null;
        }
        $text = Values::text($value);

        return array_find($this->definition->lifecycle->states, fn (State $s) => $s->key === $text);
    }

    public function initialState(): State
    {
        $states = $this->lifecycle()->states;

        return array_find($states, fn (State $s) => $s->isInitial()) ?? $states[0];
    }

    public function transitionByKey(string $key): ?Transition
    {
        return array_find($this->definition->lifecycle?->transitions ?? [], fn (Transition $t) => $t->key === $key);
    }

    public function transitionById(string $id): ?Transition
    {
        return array_find($this->definition->lifecycle?->transitions ?? [], fn (Transition $t) => $t->id === $id);
    }

    /** The variable key for a variable id. */
    public function variableKey(string $id): string
    {
        return array_find($this->definition->variables ?? [], fn (Variable $v) => $v->id === $id)?->key ?? $id;
    }
}
