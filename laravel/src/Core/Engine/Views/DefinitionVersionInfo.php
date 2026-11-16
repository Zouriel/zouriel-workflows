<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/** One published version of a definition, with how many instances are pinned to it. */
final readonly class DefinitionVersionInfo
{
    public function __construct(public int $version, public ?string $note, public string $publishedAt, public ?string $publishedBy, public int $instances) {}
}
