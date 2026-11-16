<?php

namespace Zouriel\Workflows\Core\Storage;

/** A workflow definition: its editable draft and which version is published. Port of .NET `DefinitionRecord`. */
final class DefinitionRecord
{
    public string $id = '';

    public string $key = '';

    public string $kind = '';

    public string $title = '';

    public ?string $entity = null;

    public string $status = DefinitionStatus::DRAFT;

    /** The latest published version (0 when never published). */
    public int $version = 0;

    public string $draftJson = '{}';

    public bool $hasDraftChanges = true;

    public int $createdAt = 0;

    public ?string $createdBy = null;

    public int $updatedAt = 0;

    public ?string $updatedBy = null;

    public int $rowVersion = 0;
}
