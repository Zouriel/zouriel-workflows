<?php

use Zouriel\Workflows\Laravel\Adapters\AuthActorAccessor;
use Zouriel\Workflows\Laravel\Adapters\HttpWebhookClient;
use Zouriel\Workflows\Laravel\Adapters\LaravelNotificationSender;
use Zouriel\Workflows\Laravel\Adapters\UserModelNotifiables;

return [

    /*
    | Where the REST API the @zouriel/ui workflow components call is mounted (provideUiWorkflow({ baseUrl })).
    */
    'path' => 'api/workflows',

    /*
    | The app's declaration of what workflows see and do: a class implementing
    | Zouriel\Workflows\Laravel\WorkflowRegistrar (entities bound to models, actions, signals, roles...).
    */
    'registry' => null,

    /*
    | The engine's tables (wf_definitions, wf_instances, ...): prefix and connection. The connection must be the one the
    | registered models use, so record writes and workflow rows commit in one transaction. Publish the migration with
    | `php artisan vendor:publish --tag=workflows-migrations`.
    */
    'table_prefix' => 'wf_',

    'connection' => null,

    /*
    | What the app supplies to the engine, as container classes:
    | - identity: Zouriel\Workflows\Core\Host\WorkflowIdentity (people, roles, groups, managers); null = nobody.
    | - actor: Zouriel\Workflows\Core\Host\ActorAccessor (who is writing records); default: Auth::user().
    | - notifications.sender: Zouriel\Workflows\Core\Host\NotificationSender; default: Laravel Notifications through
    |   notifications.recipients (a NotifiableResolver; default: notifications.user_model by id, else mail by address).
    | - webhooks: Zouriel\Workflows\Core\Host\WebhookClient; default: Laravel's HTTP client with the limits below.
    | - secrets: name => value, for webhook headers ({{secret:name}}).
    */
    'identity' => null,

    'actor' => AuthActorAccessor::class,

    'notifications' => [
        'sender' => LaravelNotificationSender::class,
        'recipients' => UserModelNotifiables::class,
        'user_model' => null,
    ],

    'webhooks' => [
        'client' => HttpWebhookClient::class,
        'allow_private_networks' => false,
        'max_response_bytes' => 1048576,
        'connect_timeout' => 10,
    ],

    'secrets' => [],

    /*
    | Engine options: the audit chain's tenant (apps sharing one audit table), the link put in task notifications
    | ({entity} and {id} are replaced), and how long the change hook trusts its list of lifecycle-owned fields.
    */
    'tenant' => null,

    'record_link' => null,

    'ownership_refresh_ms' => 30000,

    /*
    | The background worker (`php artisan workflows:work`, or `workflows:tick` from the scheduler): rows per round,
    | seconds to sleep when idle, and how long processed outbox rows and trigger claims are kept.
    */
    'worker' => [
        'batch' => 100,
        'sleep' => 1,
        'cleanup_every' => 3600,
        'keep_outbox_days' => 7,
        'keep_claims_days' => 30,
    ],

    /*
    | Gate names the REST API checks (defined by the app): reading definitions and runs, designing and publishing,
    | operating instances (retry, skip, cancel, migrate), reading the audit log, changing global settings.
    */
    'gates' => [
        'view' => 'workflows.view',
        'design' => 'workflows.design',
        'publish' => 'workflows.publish',
        'operate' => 'workflows.operate',
        'audit' => 'workflows.audit',
        'settings' => 'workflows.settings',
    ],

];
