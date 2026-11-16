<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** A workflow definition in lists (builder home page, API). Port of .NET `DefinitionSummary`. */
readonly class DefinitionSummary
{
    public function __construct(
        public string $id,
        public string $key,
        public string $kind,
        public string $title,
        public ?string $entity,
        public string $status,
        public int $version,
        public bool $hasDraftChanges,
        public string $updatedAt,
        public ?string $updatedBy,
        public int $runningInstances,
    ) {}
}
