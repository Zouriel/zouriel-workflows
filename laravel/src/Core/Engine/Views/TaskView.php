<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;

/** An approval or task as the inbox and record panel show it; decision answers are masked. Port of .NET `TaskView`. */
final readonly class TaskView
{
    /**
     * @param  list<string>  $assignees
     * @param  list<string>  $assigneeNames
     * @param  list<TaskDecision>  $decisions
     */
    public function __construct(
        public string $id,
        public string $instanceId,
        public string $kind,
        public string $title,
        public ?string $instructions,
        public string $definitionKey,
        public ?string $entity,
        public ?string $entityId,
        public ?string $entityTitle,
        public string $status,
        public array $assignees,
        public array $assigneeNames,
        public mixed $rule,
        public mixed $outcomes,
        public array $decisions,
        public ?stdClass $form,
        public ?string $requireComment,
        public bool $allowDelegation,
        public ?string $outcome,
        public string $dueAt,
        public bool $overdue,
        public int $escalationLevel,
        public string $createdAt,
        public ?string $completedAt,
        public bool $canDecide,
    ) {}
}
