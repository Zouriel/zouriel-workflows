<?php

namespace Zouriel\Workflows\Core\Storage;

/** Filters for listing tasks (open first, then by due time). Port of .NET `TaskQuery`. */
final readonly class TaskQuery
{
    /** @param  list<string>|null  $statuses */
    public function __construct(
        public ?string $assigneeId = null,
        public ?string $instanceId = null,
        public ?string $rootInstanceId = null,
        public ?string $entity = null,
        public ?string $entityId = null,
        public ?array $statuses = null,
        public int $skip = 0,
        public int $take = 50,
    ) {}
}
