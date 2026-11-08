<?php

use Illuminate\Support\ServiceProvider;
use Orchestra\Testbench\TestCase;
use Zouriel\Workflows\Laravel\WorkflowsServiceProvider;

uses(TestCase::class)->beforeEach(function () {
    $this->app->register(WorkflowsServiceProvider::class);
});

it('provides its configuration to the app', function () {
    expect(config('workflows.path'))->toBe('api/workflows')
        ->and(config('workflows.table_prefix'))->toBe('wf_');
});

it('lets the app publish and override the configuration', function () {
    expect(ServiceProvider::pathsToPublish(WorkflowsServiceProvider::class, 'workflows-config'))->toHaveCount(1);
});
