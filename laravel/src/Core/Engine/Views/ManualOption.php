<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;

/** A manual-trigger workflow the viewer may start from a record (spec §5 `manual`). */
final readonly class ManualOption
{
    public function __construct(public string $key, public string $label, public ?stdClass $form) {}
}
