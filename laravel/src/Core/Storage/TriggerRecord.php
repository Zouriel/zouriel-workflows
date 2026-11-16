<?php

namespace Zouriel\Workflows\Core\Storage;

/** One trigger of a published definition, or a lifecycle's owned field (`type` `lifecycle`), indexed for matching (spec §5). Port of .NET `TriggerRecord`. */
final class TriggerRecord
{
    public string $id = '';

    public string $definitionId = '';

    public string $definitionKey = '';

    public int $version = 0;

    public string $type = '';

    public ?string $entity = null;

    public ?string $field = null;

    public ?string $lifecycle = null;

    public ?string $signal = null;

    /** When the definition was first published; changes committed before it never start it (spec §5). */
    public int $publishedAt = 0;

    /** Extra matching data (JSON): lifecycle mode, initial state and appliesWhen; schedule cron. */
    public ?string $data = null;
}
