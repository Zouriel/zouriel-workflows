<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/**
 * Everything a record page shows: its lifecycle state, what the viewer may apply, open tasks, recent runs,
 * manual starts, states, the reset option, the incident error and the public processing time. Reading never
 * creates the lifecycle instance. Port of .NET `RecordView`.
 */
final readonly class RecordView
{
    /**
     * @param  list<TransitionOption>  $transitions
     * @param  list<TaskView>  $tasks
     * @param  list<InstanceView>  $instances
     * @param  list<ManualOption>  $manual
     * @param  list<\stdClass>  $states
     */
    public function __construct(
        public string $entity,
        public string $id,
        public ?string $title,
        public ?string $lifecycle,
        public ?string $lifecycleTitle,
        public ?string $state,
        public ?string $stateLabel,
        public ?string $stateTone,
        public ?string $status,
        public ?string $stateEnteredAt,
        public array $transitions,
        public array $tasks,
        public array $instances,
        public array $manual,
        public array $states,
        public ?ResetOption $reset,
        public ?string $error = null,
        public ?string $publicProcessingTime = null,
    ) {}
}
