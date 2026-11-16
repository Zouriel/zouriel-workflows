<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** An instance as the API and run viewer show it; data is masked (spec §11). Steps, tokens, outputs, variables and tasks only on single reads. */
final readonly class InstanceView
{
    /**
     * @param  list<StepView>|null  $steps
     * @param  list<TokenView>|null  $tokens
     * @param  list<TaskView>|null  $tasks
     */
    public function __construct(
        public string $id,
        public string $definitionId,
        public string $definitionKey,
        public string $title,
        public int $version,
        public string $kind,
        public ?string $parentId,
        public ?string $reason,
        public ?string $entity,
        public ?string $entityId,
        public ?string $entityTitle,
        public string $status,
        public ?string $state,
        public ?string $stateLabel,
        public ?string $error,
        public ?string $errorStepId,
        public ?string $initiatorId,
        public ?string $initiatorName,
        public string $startedAt,
        public string $updatedAt,
        public ?string $endedAt,
        public mixed $trigger,
        public ?array $steps = null,
        public ?array $tokens = null,
        public mixed $outputs = null,
        public mixed $variables = null,
        public ?array $tasks = null,
    ) {}
}
