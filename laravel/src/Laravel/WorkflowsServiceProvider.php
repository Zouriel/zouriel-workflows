<?php

namespace Zouriel\Workflows\Laravel;

use Illuminate\Contracts\Container\Container;
use Illuminate\Database\Events\TransactionRolledBack;
use Illuminate\Http\Client\Factory;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Event;
use Illuminate\Support\Facades\Log;
use Illuminate\Support\ServiceProvider;
use Zouriel\Workflows\Core\Engine\EngineOptions;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\ActionResolver;
use Zouriel\Workflows\Core\Host\ActorAccessor;
use Zouriel\Workflows\Core\Host\NoIdentity;
use Zouriel\Workflows\Core\Host\NotificationSender;
use Zouriel\Workflows\Core\Host\SecretResolver;
use Zouriel\Workflows\Core\Host\WebhookClient;
use Zouriel\Workflows\Core\Host\WorkflowIdentity;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Time\Clock;
use Zouriel\Workflows\Core\Time\SystemClock;
use Zouriel\Workflows\Laravel\Adapters\AuthActorAccessor;
use Zouriel\Workflows\Laravel\Adapters\ConfigSecrets;
use Zouriel\Workflows\Laravel\Adapters\ContainerActions;
use Zouriel\Workflows\Laravel\Adapters\HttpWebhookClient;
use Zouriel\Workflows\Laravel\Adapters\LaravelNotificationSender;
use Zouriel\Workflows\Laravel\Adapters\NotifiableResolver;
use Zouriel\Workflows\Laravel\Adapters\UserModelNotifiables;
use Zouriel\Workflows\Laravel\Changes\ModelChanges;
use Zouriel\Workflows\Laravel\Console\SeedCommand;
use Zouriel\Workflows\Laravel\Console\TickCommand;
use Zouriel\Workflows\Laravel\Console\WorkCommand;
use Zouriel\Workflows\Laravel\Eloquent\EloquentGateway;
use Zouriel\Workflows\Laravel\Http\WorkflowApi;
use Zouriel\Workflows\Laravel\Registry\ModelBindings;
use Zouriel\Workflows\Laravel\Registry\ModelRegistry;
use Zouriel\Workflows\Laravel\Storage\DatabaseSession;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;

/**
 * Connects Zouriel Workflows to a Laravel app: the registry the app declares (`config('workflows.registry')`), the
 * engine on the database store with the app's host services, the change hook of `HasWorkflows` models, and the
 * `workflows:work`, `workflows:tick` and `workflows:seed` commands, and the REST API (mounted at `workflows.path`). The engine and its store are scoped, so each request
 * or job (Octane included) gets its own write scopes. Port of .NET `AddWorkflowsCore` + `UseEntityFrameworkCore`.
 */
class WorkflowsServiceProvider extends ServiceProvider
{
    public function register(): void
    {
        $this->mergeConfigFrom(__DIR__.'/../../config/workflows.php', 'workflows');

        $this->app->singleton(ModelRegistry::class, function (Container $app) {
            $registry = new ModelRegistry;
            $registrar = config('workflows.registry');
            if ($registrar !== null) {
                $app->make($registrar)->register($registry);
            }

            return $registry;
        });
        $this->app->singleton(WorkflowRegistry::class, fn (Container $app) => $app->make(ModelRegistry::class)->build());
        $this->app->singleton(ModelBindings::class, fn (Container $app) => $app->make(ModelRegistry::class)->bindings());
        $this->app->singletonIf(Clock::class, SystemClock::class);
        $this->app->singleton(ModelChanges::class);

        $this->app->bind(WorkflowIdentity::class, fn (Container $app) => ($class = config('workflows.identity')) === null ? new NoIdentity : $app->make($class));
        $this->app->bind(ActorAccessor::class, fn (Container $app) => $app->make(config('workflows.actor') ?? AuthActorAccessor::class));
        $this->app->bind(NotifiableResolver::class, fn (Container $app) => ($class = config('workflows.notifications.recipients')) === UserModelNotifiables::class || $class === null
            ? new UserModelNotifiables(config('workflows.notifications.user_model'))
            : $app->make($class));
        $this->app->bind(NotificationSender::class, fn (Container $app) => $app->make(config('workflows.notifications.sender') ?? LaravelNotificationSender::class));
        $this->app->bind(WebhookClient::class, fn (Container $app) => ($class = config('workflows.webhooks.client')) === HttpWebhookClient::class || $class === null
            ? new HttpWebhookClient($app->make(Factory::class), (bool) config('workflows.webhooks.allow_private_networks'),
                (int) config('workflows.webhooks.max_response_bytes', 1048576), (int) config('workflows.webhooks.connect_timeout', 10))
            : $app->make($class));
        $this->app->bind(SecretResolver::class, ConfigSecrets::class);
        $this->app->bind(ActionResolver::class, ContainerActions::class);

        $this->app->scoped(DatabaseStore::class, fn (Container $app) => new DatabaseStore(
            DB::connection(config('workflows.connection')),
            $app->make(Clock::class),
            fn (DatabaseSession $session) => new EloquentGateway($session, $app->make(WorkflowRegistry::class), $app->make(ModelBindings::class)),
            (string) config('workflows.table_prefix', 'wf_'),
        ));
        $this->app->scoped(WorkflowEngine::class, fn (Container $app) => new WorkflowEngine(new EngineOptions(
            registry: $app->make(WorkflowRegistry::class),
            store: $app->make(DatabaseStore::class),
            identity: $app->make(WorkflowIdentity::class),
            notifications: $app->make(NotificationSender::class),
            webhooks: $app->make(WebhookClient::class),
            secrets: $app->make(SecretResolver::class),
            actions: $app->make(ActionResolver::class),
            clock: $app->make(Clock::class),
            tenant: config('workflows.tenant'),
            actorAccessor: $app->make(ActorAccessor::class),
            recordLinkTemplate: config('workflows.record_link'),
            ownershipRefreshMs: (int) config('workflows.ownership_refresh_ms', 30000),
            warn: fn (string $message) => Log::warning("Workflows: {$message}"),
        )));
        $this->app->bind(Worker::class, fn (Container $app) => new Worker(
            $app->make(WorkflowEngine::class),
            (int) config('workflows.worker.batch', 100),
            (int) config('workflows.worker.cleanup_every', 3600),
            (int) config('workflows.worker.keep_outbox_days', 7),
            (int) config('workflows.worker.keep_claims_days', 30),
        ));
        WorkflowApi::bindDefaults($this->app);
    }

    public function boot(): void
    {
        $this->publishes([__DIR__.'/../../config/workflows.php' => config_path('workflows.php')], 'workflows-config');
        $this->publishes([__DIR__.'/../../database/migrations' => database_path('migrations')], 'workflows-migrations');

        if ($this->app->runningInConsole()) {
            $this->commands([WorkCommand::class, TickCommand::class, SeedCommand::class]);
        }

        // A refused write to an owned field is audited in a unit of its own once the write's transaction has rolled back
        // (never on the failing transaction), at the latest when the request ends.
        Event::listen(TransactionRolledBack::class, function (TransactionRolledBack $event) {
            if ($event->connection->transactionLevel() === 0 && $this->app->resolved(WorkflowEngine::class)) {
                $this->app->make(WorkflowEngine::class)->flushBackground();
            }
        });
        $this->app->terminating(function () {
            if ($this->app->resolved(WorkflowEngine::class)) {
                $this->app->make(WorkflowEngine::class)->flushBackground();
            }
        });
        if (config('workflows.api.routes', true) && ! $this->app->routesAreCached()) {
            WorkflowApi::routes();
        }
    }
}
