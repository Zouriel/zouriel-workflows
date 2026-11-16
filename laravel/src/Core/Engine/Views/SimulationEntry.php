<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** One thing that happened (or would have happened) during a test run. Port of .NET `SimulationEntry`. */
final readonly class SimulationEntry
{
    public function __construct(public string $kind, public string $action, public ?string $subject, public ?string $text, public mixed $data, public string $at) {}
}
