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
    | The REST API (spec/api/openapi.json). Every endpoint needs a signed-in user (the ActorResolver finds
    | one) unless allow_anonymous is set; add the app's authentication middleware, e.g. ['api', 'auth:sanctum'].
    |
    | gates: the Laravel Gate (ability name) each kind of operation needs, as .NET's WorkflowApiOptions policies.
    | null = no gate (every signed-in user may). audit_export falls back to audit; signal falls back to intervene,
    | and with neither set signals cannot be raised through the API at all.
    |   view       reading workflows, instances, records and one's own tasks; deciding and delegating one's tasks
    |   edit       creating and editing drafts, validating, test runs, reading global settings
    |   publish    publishing, archiving, restoring, rolling back, deleting, changing permissions
    |   intervene  cancel, retry, skip, migrate, force state, reassign, and listing everyone's tasks
    |   audit      reading and verifying the audit log
    |   settings   changing global settings and purging the audit log
    */
    'api' => [
        'routes' => true,
        'middleware' => ['api'],
        'allow_anonymous' => false,
        'gates' => [
            'view' => null,
            'edit' => null,
            'publish' => null,
            'intervene' => null,
            'audit' => null,
            'audit_export' => null,
            'settings' => null,
            'signal' => null,
        ],
        'max_body_bytes' => 2 * 1024 * 1024,
        'max_decision_data_bytes' => 256 * 1024,
        'max_simulation_events' => 50,
    ],

];
