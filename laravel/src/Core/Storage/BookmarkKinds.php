<?php

namespace Zouriel\Workflows\Core\Storage;

/** What a bookmark waits for. */
final class BookmarkKinds
{
    public const string TIMER = 'timer';

    public const string TASK = 'task';

    public const string SIGNAL = 'signal';

    public const string CONDITION = 'condition';

    public const string CHILD = 'child';

    /** A schedule trigger's next run (no instance). */
    public const string SCHEDULE = 'schedule';

    /** A dateReached trigger's time for one record (no instance). */
    public const string DATE_REACHED = 'dateReached';
}
