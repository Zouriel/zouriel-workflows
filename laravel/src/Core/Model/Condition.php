<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\JsonShapeException;
use Zouriel\Workflows\Core\Json\Read;

/**
 * A condition rule or group, the form builder's JSON (spec §4). Port of .NET `Condition` and
 * `ConditionConverter`: an object with a `conditions` array is a group (items that are not objects
 * are dropped), anything else a rule. `toJson()` gives the condition `WorkflowConditions` evaluates.
 */
abstract class Condition
{
    public static function fromJson(mixed $value, string $path): ?self
    {
        if ($value === null) {
            return null;
        }
        if (! $value instanceof stdClass) {
            throw new JsonShapeException("A condition must be an object. Path: {$path}.");
        }

        return self::read($value, $path);
    }

    private static function read(stdClass $node, string $path): self
    {
        if (is_array($node->conditions ?? null)) {
            $group = new ConditionGroup;
            $group->match = Read::string($node, 'match', $path, 'all');
            foreach ($node->conditions as $i => $item) {
                if ($item instanceof stdClass) {
                    $group->conditions[] = self::read($item, "{$path}.conditions[{$i}]");
                }
            }

            return $group;
        }
        $rule = new ConditionRule;
        $rule->ref = Read::string($node, 'ref', $path);
        $rule->op = Read::string($node, 'op', $path, 'eq');
        $rule->value = ($node->value ?? null) === null ? null : Operand::fromJson($node->value, "{$path}.value");
        $rule->value2 = ($node->value2 ?? null) === null ? null : Operand::fromJson($node->value2, "{$path}.value2");

        return $rule;
    }

    abstract public function toJson(): stdClass;

    /** Whether every rule has what it needs: a path, and values where its operator needs them (.NET `Conditions.IsComplete`). */
    abstract public function isComplete(): bool;

    /** @return list<string> every token path the condition reads, operands and calculations included (.NET `Conditions.Refs`) */
    abstract public function refs(): array;

    /** How many values an operator needs (0, 1 or 2), as .NET `Conditions.Arity`. */
    public static function arity(string $op): int
    {
        return match ($op) {
            'empty', 'notEmpty', 'isTrue', 'isFalse', 'visible', 'hidden', 'complete', 'incomplete' => 0,
            'between' => 2,
            default => 1,
        };
    }
}
