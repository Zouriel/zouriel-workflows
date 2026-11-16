<?php

namespace Zouriel\Workflows\Core\Engine;

use Zouriel\Workflows\Core\Model\Step;

/** Where a step sits: the list that holds it and its index there. Port of .NET `Engine/CompiledDefinition.cs` `StepLocation`. */
final readonly class StepLocation
{
    public function __construct(public Step $step, public string $listId, public int $index, public ?string $parentStepId) {}
}
