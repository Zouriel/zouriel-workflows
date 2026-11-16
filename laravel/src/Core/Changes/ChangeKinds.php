<?php

namespace Zouriel\Workflows\Core\Changes;

/** Kinds of record change (spec §5 triggers). */
final class ChangeKinds
{
    public const string CREATED = 'created';

    public const string UPDATED = 'updated';

    public const string DELETED = 'deleted';
}
