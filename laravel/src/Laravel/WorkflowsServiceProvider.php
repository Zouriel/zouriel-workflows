<?php

namespace Zouriel\Workflows\Laravel;

use Illuminate\Support\ServiceProvider;

/**
 * Connects Zouriel Workflows to a Laravel app. Today it only provides the configuration; the
 * engine, its tables, routes and worker are added phase by phase (docs/plans/go-and-laravel.md).
 */
class WorkflowsServiceProvider extends ServiceProvider
{
    public function register(): void
    {
        $this->mergeConfigFrom(__DIR__.'/../../config/workflows.php', 'workflows');
    }

    public function boot(): void
    {
        $this->publishes([__DIR__.'/../../config/workflows.php' => config_path('workflows.php')], 'workflows-config');
    }
}
