<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;

/** A transition the viewer may apply to a record now (its `who` and guard hold, spec §6). */
final readonly class TransitionOption
{
    public function __construct(
        public string $key,
        public string $label,
        public string $to,
        public string $toLabel,
        public ?string $tone,
        public ?string $confirm,
        public ?stdClass $form,
        public string $comment,
    ) {}
}
