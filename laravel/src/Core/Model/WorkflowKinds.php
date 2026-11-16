<?php

namespace Zouriel\Workflows\Core\Model;

/** The two kinds of workflow definition. Port of .NET `WorkflowKinds`. */
final class WorkflowKinds
{
    /** A trigger followed by steps. */
    public const string AUTOMATION = 'automation';

    /** States and transitions owning one enum field of a record. */
    public const string LIFECYCLE = 'lifecycle';
}
