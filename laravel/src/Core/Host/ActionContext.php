<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;

/**
 * What an app action handler is given (spec §7 `action`): the evaluated inputs, the record, the
 * instance, an idempotency key that is the same for every attempt of one run of the step, the
 * 1-based attempt, the initiator and whether this is a test run. Port of .NET `WorkflowActionContext`.
 */
final readonly class ActionContext
{
    public function __construct(
        public string $actionKey,
        public stdClass $input,
        public ?string $entity,
        public ?string $entityId,
        public string $instanceId,
        public string $idempotencyKey,
        public int $attempt,
        public ?WorkflowActor $initiator = null,
        public bool $simulation = false,
    ) {}
}
