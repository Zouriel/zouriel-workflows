<?php

use Illuminate\Foundation\Auth\User;
use Illuminate\Support\Facades\Artisan;
use Illuminate\Support\Facades\Auth;
use Illuminate\Support\ServiceProvider;
use Orchestra\Testbench\TestCase;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\ActorAccessor;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;
use Zouriel\Workflows\Laravel\WorkflowsServiceProvider;
use Zouriel\Workflows\Tests\Support\App\TestRegistrar;

uses(TestCase::class)->beforeEach(function () {
    config(['workflows.registry' => TestRegistrar::class, 'database.default' => 'testing',
        'database.connections.testing' => ['driver' => 'sqlite', 'database' => ':memory:']]);
    $this->app->register(WorkflowsServiceProvider::class);
});

it('provides its configuration to the app', function () {
    expect(config('workflows.path'))->toBe('api/workflows')
        ->and(config('workflows.table_prefix'))->toBe('wf_');
});

it('lets the app publish the configuration and the migration', function () {
    expect(ServiceProvider::pathsToPublish(WorkflowsServiceProvider::class, 'workflows-config'))->toHaveCount(1)
        ->and(array_map(realpath(...), array_keys(ServiceProvider::pathsToPublish(WorkflowsServiceProvider::class, 'workflows-migrations'))))
        ->toBe([realpath(__DIR__.'/../../database/migrations')]);
});

it('builds the registry the app declares and the engine on the database store', function () {
    $engine = $this->app->make(WorkflowEngine::class);

    expect($this->app->make(WorkflowRegistry::class)->entity('application')->field('status')->isLifecycle())->toBeTrue()
        ->and($engine->options->store)->toBe($this->app->make(DatabaseStore::class))
        ->and($engine->options->store->prefix)->toBe('wf_')
        ->and($engine)->toBe($this->app->make(WorkflowEngine::class));
});

it('gives each request its own engine (and write scopes)', function () {
    $first = $this->app->make(WorkflowEngine::class);
    $this->app->forgetScopedInstances();

    expect($this->app->make(WorkflowEngine::class))->not->toBe($first);
});

it('attributes writes to the signed-in user', function () {
    expect($this->app->make(ActorAccessor::class)->current())->toBeNull();
    $user = new User;
    $user->forceFill(['id' => 7, 'name' => 'Aminath']);
    Auth::setUser($user);

    $actor = $this->app->make(ActorAccessor::class)->current();
    expect([$actor->type, $actor->id, $actor->name])->toBe(['user', '7', 'Aminath']);
});

it('registers the worker commands', function () {
    $commands = array_keys(Artisan::all());

    expect($commands)->toContain('workflows:work', 'workflows:tick', 'workflows:seed');
});
