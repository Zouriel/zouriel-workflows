<?php

namespace Zouriel\Workflows\Core\Storage;

/** An immutable published version of a definition. Port of .NET `DefinitionVersionRecord`. */
final class DefinitionVersionRecord
{
    public string $definitionId = '';

    public int $version = 0;

    public string $key = '';

    /** The published JSON (with `revision` set to the version). */
    public string $json = '{}';

    /** SHA-256 of the canonical JSON, to tell whether a draft differs. */
    public string $hash = '';

    public ?string $note = null;

    public int $publishedAt = 0;

    public ?string $publishedBy = null;
}
