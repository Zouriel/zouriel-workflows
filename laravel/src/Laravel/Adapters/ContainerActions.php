<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Contracts\Container\Container;
use Zouriel\Workflows\Core\Host\ActionResolver;
use Zouriel\Workflows\Core\Host\WorkflowAction;
use Zouriel\Workflows\Core\Registry\ActionDescriptor;

/** Runs each registered action with its handler class, made by the container (so handlers get the app's services). */
final class ContainerActions implements ActionResolver
{
    public function __construct(private readonly Container $app) {}

    public function resolve(ActionDescriptor $action): ?WorkflowAction
    {
        if ($action->handler === null || ! class_exists($action->handler)) {
            return null;
        }
        $handler = $this->app->make($action->handler);

        return $handler instanceof WorkflowAction ? $handler : null;
    }
}
