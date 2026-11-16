<?php

return [

    /*
    | Where the REST API the @zouriel/ui workflow components call is mounted
    | (provideUiWorkflow({ baseUrl })).
    */
    'path' => 'api/workflows',

    /*
    | Table prefix and connection for the engine's tables (wf_definitions, wf_instances, ...).
    */
    'table_prefix' => 'wf_',

    'connection' => null,

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
