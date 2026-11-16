<?php

namespace Zouriel\Workflows\Core\Lint;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Model\Step;
use Zouriel\Workflows\Core\Model\Transition;
use Zouriel\Workflows\Core\Model\UnknownStep;
use Zouriel\Workflows\Core\Model\WorkflowDefinition;

/**
 * Plain-English differences between two versions of a workflow, shown before publishing and stored in
 * the audit log. Port of .NET `Lint/DefinitionDiff.cs`: parts are compared in their model JSON form, so
 * members the model does not know never count as changes.
 */
final class DefinitionDiff
{
    /** @return list<string> one sentence per change from `$before` to `$after` (empty when they match) */
    public static function describe(WorkflowDefinition $before, WorkflowDefinition $after): array
    {
        $lines = [];
        if ($before->title !== $after->title) {
            $lines[] = "Renamed from “{$before->title}” to “{$after->title}”.";
        }
        if (Json::encode($before->trigger?->toJson()) !== Json::encode($after->trigger?->toJson())) {
            $lines[] = 'Changed what starts the workflow.';
        }

        $a = (new CompiledDefinition($before, $before->id, 0))->steps;
        $b = (new CompiledDefinition($after, $after->id, 0))->steps;
        foreach ($b as $id => $location) {
            if (! array_key_exists($id, $a)) {
                $lines[] = 'Added step “'.self::label($location->step).'”.';
            }
        }
        foreach ($a as $id => $location) {
            if (! array_key_exists($id, $b)) {
                $lines[] = 'Removed step “'.self::label($location->step).'”.';
            }
        }
        foreach ($b as $id => $location) {
            if (! array_key_exists($id, $a)) {
                continue;
            }
            $old = $a[$id];
            if ($old->listId !== $location->listId || $old->index !== $location->index) {
                $lines[] = 'Moved step “'.self::label($location->step).'”.';
            }
            if (self::stripped($old->step) !== self::stripped($location->step)) {
                $lines[] = 'Changed step “'.self::label($location->step).'”.';
            }
        }

        $la = $before->lifecycle;
        $lb = $after->lifecycle;
        if ($la !== null && $lb !== null) {
            $stateIn = fn (array $states, string $id) => array_find($states, fn (State $s) => $s->id === $id);
            $transitionIn = fn (array $transitions, string $id) => array_find($transitions, fn (Transition $t) => $t->id === $id);
            foreach ($lb->states as $s) {
                if ($stateIn($la->states, $s->id) === null) {
                    $lines[] = "Added state “{$s->label}”.";
                }
            }
            foreach ($la->states as $s) {
                if ($stateIn($lb->states, $s->id) === null) {
                    $lines[] = "Removed state “{$s->label}”.";
                }
            }
            foreach ($lb->states as $s) {
                $o = $stateIn($la->states, $s->id);
                if ($o === null) {
                    continue;
                }
                if ($o->label !== $s->label) {
                    $lines[] = "Renamed state “{$o->label}” to “{$s->label}”.";
                }
                if ($o->key !== $s->key) {
                    $lines[] = "State “{$s->label}” now stores “{$s->key}” instead of “{$o->key}”.";
                }
                if ($o->category !== $s->category) {
                    $lines[] = "State “{$s->label}” is now {$s->category}.";
                }
            }
            foreach ($lb->transitions as $t) {
                if ($transitionIn($la->transitions, $t->id) === null) {
                    $lines[] = "Added transition “{$t->label}”.";
                }
            }
            foreach ($la->transitions as $t) {
                if ($transitionIn($lb->transitions, $t->id) === null) {
                    $lines[] = "Removed transition “{$t->label}”.";
                }
            }
            foreach ($lb->transitions as $t) {
                $o = $transitionIn($la->transitions, $t->id);
                if ($o === null) {
                    continue;
                }
                if ($o->label !== $t->label) {
                    $lines[] = "Renamed transition “{$o->label}” to “{$t->label}”.";
                }
                if (self::strippedTransition($o) !== self::strippedTransition($t)) {
                    $lines[] = "Changed who can do or what happens on “{$t->label}”.";
                }
            }
        }
        // Something differs that no sentence above names (positions, descriptions…); identical versions say nothing.
        if ($lines === [] && Definitions::serialize($before) !== Definitions::serialize($after)) {
            $lines[] = 'Only layout or wording changes.';
        }

        return $lines;
    }

    private static function label(Step $s): string
    {
        return $s->title === null || trim($s->title) === '' ? (Linter::STEP_LABELS[$s->type] ?? $s->type) : $s->title;
    }

    /** The step's JSON without its nested lists, so a child change is reported once, on the child. */
    private static function stripped(Step $s): string
    {
        $node = $s->toJson();
        foreach (['paths', 'otherwise', 'cases', 'steps', 'outcomes', 'onTimeout'] as $key) {
            if (($node->{$key} ?? null) !== null) {
                $node->{$key} = self::stripLists(Json::copy($node->{$key}));
            }
        }

        return Json::encode(Step::fromJson(Json::normalize($node), '$')->toJson());
    }

    private static function stripLists(mixed $node): mixed
    {
        if ($node instanceof stdClass) {
            if (is_array($node->steps ?? null)) {
                $node->steps = [];
            }
            foreach (['approved', 'rejected', 'timedOut'] as $key) {
                if (is_array($node->{$key} ?? null)) {
                    $node->{$key} = [];
                }
            }

            return $node;
        }
        if (is_array($node)) {
            if (array_all($node, fn ($x) => $x instanceof stdClass && ($x->type ?? null) !== null)) {
                return [];
            }

            return array_map(self::stripLists(...), $node);
        }

        return $node;
    }

    /** Who may apply it and what it does: the members the "Changed who can do…" sentence is about (not tone, confirm or remarks). */
    private static function strippedTransition(Transition $t): string
    {
        $copy = new Transition;
        $copy->id = $t->id;
        $copy->key = $t->key;
        $copy->from = $t->from;
        $copy->to = $t->to;
        $copy->who = $t->who;
        $copy->when = $t->when;
        $copy->input = $t->input;
        $copy->auto = $t->auto;
        $copy->steps = $t->steps === null ? null : array_map(function (Step $s) {
            $stub = new UnknownStep;
            $stub->id = $s->id;
            $stub->type = $s->type;

            return $stub;
        }, $t->steps);

        return Json::encode($copy->toJson());
    }
}
