<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;

/**
 * What a test run (`POST /simulate`) should do: which record to use (its values read, never written, with `values` on
 * top), how the workflow starts (an automation's trigger data, or a lifecycle's state and transition), the form answers,
 * and what happens next (at most 50 events). Port of .NET `SimulationRequest`.
 */
final class SimulationRequest
{
    /** @param  list<SimulationEvent>  $events */
    public function __construct(
        public ?string $entityId = null,
        public ?stdClass $values = null,
        public ?stdClass $trigger = null,
        public ?string $fromState = null,
        public ?string $transition = null,
        public ?stdClass $input = null,
        public array $events = [],
    ) {}
}
