<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Workflows\Core\Expressions\WorkflowConditions;
use Zouriel\Workflows\Core\Model\Condition;

/**
 * Whether a lifecycle applies to a record (spec §6 applies to). The condition reads only the record's own
 * fields (`entity.<field>`), so it can be checked from the values being written. Port of .NET `Engine/LifecycleSelection.cs`.
 */
final class LifecycleSelection
{
    public static function applies(?Condition $appliesWhen, ?stdClass $values, string $today): bool
    {
        return $appliesWhen === null || WorkflowConditions::evaluate($appliesWhen->toJson(),
            fn (string $path) => str_starts_with($path, 'entity.') && $values !== null ? ($values->{substr($path, 7)} ?? null) : null, $today);
    }
}
