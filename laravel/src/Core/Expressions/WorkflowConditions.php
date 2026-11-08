<?php

namespace Zouriel\Workflows\Core\Expressions;

use stdClass;
use Zouriel\Forms\ConditionContext;
use Zouriel\Forms\Conditions;

/**
 * Workflow conditions (spec §4): the form builder's condition language with token paths as
 * references, minus the form-only operators. Those never match in a workflow, as in .NET's
 * `Expressions/Conditions.cs` (where `hidden` is false too, not "not visible"); lint refuses
 * them in definitions (`fixtures/lint/duplicate-ids-unfinished-conditions-form-only-operators.json`).
 */
final class WorkflowConditions
{
    public const array FORM_ONLY = ['visible', 'hidden', 'complete', 'incomplete', 'rowsGte', 'rowsLte'];

    /** @param  callable(string): mixed  $read  the value at a token path, or null */
    public static function evaluate(?stdClass $condition, callable $read, string $today): bool
    {
        return self::holds($condition, self::context($read, $today));
    }

    /** Whether a condition uses an operator that only exists in forms. */
    public static function usesFormOnly(?stdClass $condition): bool
    {
        if ($condition === null) {
            return false;
        }
        if (property_exists($condition, 'conditions')) {
            return array_any($condition->conditions ?? [], self::usesFormOnly(...));
        }

        return in_array($condition->op ?? '', self::FORM_ONLY, true);
    }

    private static function holds(?stdClass $condition, ConditionContext $ctx): bool
    {
        if ($condition === null) {
            return true;
        }
        if (property_exists($condition, 'conditions')) {
            $items = $condition->conditions ?? [];
            if ($items === []) {
                return true;
            }

            return match ($condition->match ?? 'all') {
                'all' => array_all($items, fn ($c) => self::holds($c, $ctx)),
                'any' => array_any($items, fn ($c) => self::holds($c, $ctx)),
                default => ! array_any($items, fn ($c) => self::holds($c, $ctx)),
            };
        }

        return ! in_array($condition->op ?? '', self::FORM_ONLY, true) && Conditions::evaluate($condition, $ctx);
    }

    private static function context(callable $read, string $today): ConditionContext
    {
        return new readonly class($read(...), $today) implements ConditionContext
        {
            public function __construct(private \Closure $read, private string $todayIso) {}

            public function value(string $ref): mixed
            {
                return ($this->read)($ref);
            }

            public function visible(string $ref): bool
            {
                return false;
            }

            public function complete(string $ref): bool
            {
                return false;
            }

            public function rows(string $ref): ?int
            {
                return null;
            }

            public function today(): string
            {
                return $this->todayIso;
            }
        };
    }
}
