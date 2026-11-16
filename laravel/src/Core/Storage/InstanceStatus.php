<?php

namespace Zouriel\Workflows\Core\Storage;

/** Instance statuses (spec §10). Port of .NET `InstanceStatus`. */
final class InstanceStatus
{
    public const string QUEUED = 'queued';

    public const string RUNNING = 'running';

    public const string WAITING = 'waiting';

    public const string INCIDENT = 'incident';

    public const string COMPLETED = 'completed';

    public const string FAILED = 'failed';

    public const string CANCELLED = 'cancelled';

    /** Record instances: in a non-terminal state. */
    public const string ACTIVE = 'active';

    /** Record instances: the field holds a value the lifecycle does not agree with. */
    public const string OUT_OF_SYNC = 'outOfSync';

    public static function isFinished(string $status): bool
    {
        return in_array($status, [self::COMPLETED, self::FAILED, self::CANCELLED], true);
    }
}
