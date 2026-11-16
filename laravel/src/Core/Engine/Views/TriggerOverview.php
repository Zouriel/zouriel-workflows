<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** One active trigger or lifecycle, for the "Watching" section of global settings. */
final readonly class TriggerOverview
{
    public function __construct(
        public string $definitionId,
        public string $definitionKey,
        public string $title,
        public string $kind,
        public string $type,
        public ?string $entity,
        public ?string $field,
        public string $summary,
        public int $version,
    ) {}
}
