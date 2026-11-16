<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** One thread of an instance and what it waits for. */
final readonly class TokenView
{
    public function __construct(public string $id, public string $status, public ?string $waiting, public ?string $stepId) {}
}
