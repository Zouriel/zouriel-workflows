<?php

namespace Zouriel\Workflows\Core\Storage;

/** The lifecycle of a workflow definition (spec §13). Port of .NET `Storage/Records.cs` `DefinitionStatus`. */
final class DefinitionStatus
{
    public const string DRAFT = 'draft';

    public const string PUBLISHED = 'published';

    public const string ARCHIVED = 'archived';
}
