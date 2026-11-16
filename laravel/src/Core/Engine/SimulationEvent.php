<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;

/**
 * One thing to do during a test run after the workflow starts: `decide` an open task, `advance` the clock, raise a
 * `signal`, or `set` record fields. Port of .NET `Engine/WorkflowEngine.Simulate.cs` `SimulationEvent`.
 */
final class SimulationEvent
{
    public function __construct(
        public string $type = '',
        public ?string $step = null,
        public ?string $outcome = null,
        public ?string $comment = null,
        public ?stdClass $data = null,
        public ?string $user = null,
        public ?string $duration = null,
        public ?string $signal = null,
        public ?stdClass $values = null,
    ) {}
}
