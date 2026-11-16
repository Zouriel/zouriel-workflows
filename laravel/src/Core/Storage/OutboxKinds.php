<?php

namespace Zouriel\Workflows\Core\Storage;

/** What an outbox row carries. */
final class OutboxKinds
{
    /** A committed record change (`EntityChange`). */
    public const string CHANGE = 'change';

    /** A lifecycle transition (`LifecycleEvent`). */
    public const string LIFECYCLE = 'lifecycle';

    /** A notification to deliver. */
    public const string NOTIFY = 'notify';
}
