<?php

namespace Zouriel\Workflows\Core\Storage;

/** Statuses of a person's task. */
final class TaskStatus
{
    public const string OPEN = 'open';

    public const string COMPLETED = 'completed';

    public const string CANCELLED = 'cancelled';

    public const string TIMED_OUT = 'timedOut';
}
