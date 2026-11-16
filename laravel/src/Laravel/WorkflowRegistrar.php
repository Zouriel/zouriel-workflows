<?php

namespace Zouriel\Workflows\Laravel;

use Zouriel\Workflows\Laravel\Registry\ModelRegistry;

/**
 * Declares what workflows can see and do in the app: its entities (bound to Eloquent models), actions, signals, roles
 * and other lists. Named in `config('workflows.registry')` and resolved from the container.
 */
interface WorkflowRegistrar
{
    public function register(ModelRegistry $registry): void;
}
