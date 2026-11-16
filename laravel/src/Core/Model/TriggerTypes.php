<?php

namespace Zouriel\Workflows\Core\Model;

/** The trigger `type` values (spec §5). Port of .NET `TriggerTypes`. */
final class TriggerTypes
{
    public const string CREATED = 'created';

    public const string FIELD_CHANGED = 'fieldChanged';

    public const string STATE_ENTERED = 'stateEntered';

    public const string TRANSITION_APPLIED = 'transitionApplied';

    public const string DELETED = 'deleted';

    public const string DATE_REACHED = 'dateReached';

    public const string SCHEDULE = 'schedule';

    public const string MANUAL = 'manual';

    public const string SIGNAL = 'signal';
}
