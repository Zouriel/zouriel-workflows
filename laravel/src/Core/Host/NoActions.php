<?php

namespace Zouriel\Workflows\Core\Host;

use Zouriel\Workflows\Core\Registry\ActionDescriptor;

/** The default resolver: creates a handler from the action's `handler` class name, when it has one. */
final class NoActions implements ActionResolver
{
    public function resolve(ActionDescriptor $action): ?WorkflowAction
    {
        $handler = $action->handler !== null && class_exists($action->handler) ? new ($action->handler) : null;

        return $handler instanceof WorkflowAction ? $handler : null;
    }
}
