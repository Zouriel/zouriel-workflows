<?php

namespace Zouriel\Workflows\Core\Storage;

/** Statuses of one step execution. */
final class StepStatus
{
    public const string RUNNING = 'running';

    public const string WAITING = 'waiting';

    public const string COMPLETED = 'completed';

    public const string FAILED = 'failed';

    public const string SKIPPED = 'skipped';

    public const string CANCELLED = 'cancelled';
}
