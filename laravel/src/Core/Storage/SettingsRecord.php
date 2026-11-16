<?php

namespace Zouriel\Workflows\Core\Storage;

/** The stored global settings (spec §12), with a revision for optimistic updates. */
final class SettingsRecord
{
    public string $key = 'global';

    public string $json = '{"version":1}';

    public int $revision = 0;

    public int $updatedAt = 0;

    public ?string $updatedBy = null;
}
