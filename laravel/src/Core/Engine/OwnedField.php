<?php

namespace Zouriel\Workflows\Core\Engine;

use Zouriel\Workflows\Core\Model\Condition;

/** A lifecycle-owned field, as the change hook sees it. Port of .NET `WorkflowEngine.OwnedField`. */
final readonly class OwnedField
{
    public function __construct(
        public string $definitionId,
        public string $definitionKey,
        public string $entity,
        public string $field,
        public bool $enforce,
        public ?string $initialState,
        public ?Condition $appliesWhen,
        public int $publishedAt,
    ) {}
}
