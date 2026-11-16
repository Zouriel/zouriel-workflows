<?php

namespace Zouriel\Workflows\Core\Storage;

/** Filters for listing instances (newest first). Port of .NET `InstanceQuery`. */
final readonly class InstanceQuery
{
    /**
     * @param  list<string>|null  $statuses
     * @param  list<string>|null  $kinds
     */
    public function __construct(
        public ?string $definitionId = null,
        public ?string $definitionKey = null,
        public ?string $entity = null,
        public ?string $entityId = null,
        public ?array $statuses = null,
        public ?array $kinds = null,
        public ?string $parentId = null,
        public int $skip = 0,
        public int $take = 50,
    ) {}
}
