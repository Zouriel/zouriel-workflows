<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Workflows\Core\Host\WorkflowActor;

/** An automation an event may start, worked out in one unit and started in its own. Port of .NET `WorkflowEngine.EventStart`. */
final readonly class EventStart
{
    public function __construct(
        public string $definitionId,
        public int $version,
        public ?string $entity,
        public ?string $entityId,
        public string $eventId,
        public TriggerInfo $info,
        public WorkflowActor $actor,
        public int $depth,
        public ?string $causationDefinitionId,
        public ?stdClass $snapshot,
        public ?string $correlationId,
    ) {}
}
