<?php

return [

    /*
    | Where the REST API the @zouriel/ui workflow components call is mounted
    | (provideUiWorkflow({ baseUrl })). Routes are registered once the engine's API exists.
    */
    'path' => 'api/workflows',

    /*
    | Table prefix and connection for the engine's tables (wf_definitions, wf_instances, ...).
    */
    'table_prefix' => 'wf_',

    'connection' => null,

];
