<?php

namespace Zouriel\Workflows\Core\Storage;

/** The kinds of instance: an automation run, a lifecycle's record instance, or a child run of one (transition, onEnter, onExit, SLA, reset). */
final class InstanceKinds
{
    public const string AUTOMATION = 'automation';

    public const string RECORD = 'record';

    public const string RUN = 'run';
}
