<?php

namespace Zouriel\Workflows\Core\Storage;

/** What a bookmark does when it fires. */
final class BookmarkPurposes
{
    public const string WAIT = 'wait';

    public const string TIMEOUT = 'timeout';

    public const string RETRY = 'retry';

    public const string DUE = 'due';

    public const string REMINDER = 'reminder';

    public const string ESCALATION = 'escalation';

    public const string CHILD = 'child';

    public const string CONDITION = 'condition';

    public const string SIGNAL = 'signal';

    public const string SCHEDULE = 'schedule';

    public const string DATE_REACHED = 'dateReached';

    /** Prefix of an automatic transition's timer (`auto:TRANSITION_ID`). */
    public const string AUTO_PREFIX = 'auto:';

    /** Prefix of a state's SLA timer (`sla:STATE_ID`). */
    public const string SLA_PREFIX = 'sla:';
}
