<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** What a reset would do: its option and the workflow's work it can take back, with a token that detects changes since. */
final readonly class ResetPreview
{
    /** @param  list<ResetItem>  $items */
    public function __construct(public ResetOption $reset, public array $items, public string $token) {}
}
