<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;

/** An app-provided step (spec §7 `action`). Port of .NET `IWorkflowAction`. */
interface WorkflowAction
{
    /** Runs the action inside the unit of work and returns the declared outputs. Throwing fails the attempt and undoes its record writes. */
    public function execute(ActionContext $context): stdClass;
}
