<?php

namespace Zouriel\Workflows\Core\Host;

use Zouriel\Workflows\Core\Registry\ActionDescriptor;

/** Resolves action handlers (from the container in Laravel apps). Port of .NET `IWorkflowActionResolver`. */
interface ActionResolver
{
    /** The handler for an action, or null when none is registered. */
    public function resolve(ActionDescriptor $action): ?WorkflowAction;
}
