<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;

/** Starting the record's workflow over, as offered to the people the lifecycle's reset allows (spec §6). */
final readonly class ResetOption
{
    public function __construct(public string $label, public string $to, public string $toLabel, public ?string $confirm, public ?stdClass $form, public string $comment) {}
}
