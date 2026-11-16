<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;

/** What an undo is given: the earlier run's input and output, the record, who resets, and a stable key. Port of .NET `WorkflowActionUndoContext`. */
final readonly class ActionUndoContext
{
    public function __construct(
        public string $actionKey,
        public stdClass $input,
        public stdClass $output,
        public ?string $entity,
        public ?string $entityId,
        public WorkflowActor $actor,
        public string $idempotencyKey,
    ) {}
}
