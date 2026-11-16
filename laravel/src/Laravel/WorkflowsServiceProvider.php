<?php

namespace Zouriel\Workflows\Laravel;

use Illuminate\Support\ServiceProvider;
use Zouriel\Workflows\Laravel\Http\WorkflowApi;

/**
 * Connects Zouriel Workflows to a Laravel app: the configuration and the REST API (mounted at `workflows.path`). The
 * engine itself (`WorkflowEngine`) comes from the container; its tables and worker are added phase by phase
 * (docs/plans/go-and-laravel.md).
 */
class WorkflowsServiceProvider extends ServiceProvider
{
    public function register(): void
    {
        $this->mergeConfigFrom(__DIR__.'/../../config/workflows.php', 'workflows');
        WorkflowApi::bindDefaults($this->app);
    }

    public function boot(): void
    {
        $this->publishes([__DIR__.'/../../config/workflows.php' => config_path('workflows.php')], 'workflows-config');
        if (config('workflows.api.routes', true) && ! $this->app->routesAreCached()) {
            WorkflowApi::routes();
        }
    }
}
