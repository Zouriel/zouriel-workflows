<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** One executed step of an instance, as the run viewer shows it (masked, spec §11). */
final readonly class StepView
{
    public function __construct(
        public string $id,
        public string $nodeId,
        public string $type,
        public ?string $title,
        public int $attempt,
        public string $status,
        public mixed $input,
        public mixed $output,
        public ?string $error,
        public string $startedAt,
        public ?string $endedAt,
    ) {}
}
