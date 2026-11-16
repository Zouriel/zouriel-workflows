<?php

use Illuminate\Routing\Route as LaravelRoute;
use Illuminate\Support\Facades\Gate;
use Illuminate\Support\Facades\Route;
use Zouriel\Workflows\Tests\Laravel\Http\Definitions;
use Zouriel\Workflows\Tests\Laravel\Http\OpenApiContract;

/*
 * The OpenAPI contract (plan 4.2): the API serves exactly the operations of spec/api/openapi.json, and a scenario that
 * calls every one of them gets responses matching the documented schemas (ApiTestCase::api() validates each).
 */

beforeEach(function () {
    if (! specAvailable()) {
        $this->markTestSkipped('spec/ is not next to this package (split repo).');
    }
});

it('serves exactly the operations of the spec', function () {
    $served = collect(Route::getRoutes()->getRoutes())
        ->filter(fn (LaravelRoute $r) => str_starts_with($r->uri(), 'api/workflows'))
        ->flatMap(fn (LaravelRoute $r) => array_map(fn (string $m) => "{$m} /{$r->uri()}", array_diff($r->methods(), ['HEAD'])))
        ->sort()->values()->all();
    $spec = collect(OpenApiContract::operations())->sort()->values()->all();

    expect($spec)->toHaveCount(47)
        ->and(array_values(array_diff($spec, $served)))->toBe([])
        ->and(array_values(array_diff($served, $spec)))->toBe([]);
});

it('answers every operation with a response that matches its schema', function () {
    OpenApiContract::$seen = [];
    config(['workflows.api.gates.signal' => 'raise-signals']);
    Gate::define('raise-signals', fn ($user) => in_array('admin', $user->roles ?? [], true));
    $this->host->actions->set('crm.ping', failTimes: 2);
    $this->signIn('admin');

    // Schema, settings, directory, me
    $this->api('GET', '/schema')->assertOk();
    $this->api('GET', '/me')->assertOk();
    $revision = $this->api('GET', '/settings')->assertOk()->json('revision');
    $this->api('PUT', '/settings', ['settings' => ['version' => 1], 'revision' => $revision])->assertOk();
    $this->api('GET', '/directory/users?q=re&take=5')->assertOk();
    $this->api('GET', '/directory/users/rev1')->assertOk();
    $this->api('GET', '/directory/users/nobody')->assertNotFound();

    // Definitions
    $lifecycle = Definitions::lifecycle();
    $this->api('POST', '/definitions', $lifecycle)->assertOk();
    $this->api('PUT', '/definitions/lc_app', $lifecycle)->assertOk();
    $this->api('POST', '/validate', $lifecycle)->assertOk();
    $this->api('POST', '/definitions/lc_app/publish', ['note' => 'First'])->assertOk();
    $this->api('GET', '/definitions')->assertOk();
    $this->api('GET', '/definitions/lc_app')->assertOk();
    $this->api('GET', '/definitions/lc_app/versions/1')->assertOk();
    $this->api('GET', '/definitions/lc_app/diff')->assertOk();
    $this->api('POST', '/definitions/lc_app/permissions', ['changes' => [['node' => 'tr_no', 'who' => [['role' => 'admin']]]]])->assertOk();
    $this->api('POST', '/definitions/lc_app/rollback', ['version' => 1])->assertOk();
    foreach ([Definitions::signOff(), Definitions::waitForPayment(), Definitions::ping(), Definitions::standalone()] as $definition) {
        $this->publishDefinition($definition);
    }
    $this->api('GET', '/triggers')->assertOk();
    $this->api('POST', '/definitions/wf_alone/archive')->assertOk();
    $this->api('POST', '/definitions/wf_alone/restore')->assertOk();
    $draft = [...Definitions::standalone(), 'id' => 'wf_draft', 'key' => 'draftOnly'];
    $this->api('POST', '/definitions', $draft)->assertOk();
    $this->api('DELETE', '/definitions/wf_draft')->assertNoContent();

    // Records
    $this->record('a1', ['title' => 'Grant', 'applicant' => 'ali', 'amount' => 500]);
    $this->api('GET', '/records/application/a1')->assertOk();
    $this->signIn('ali');
    $this->api('POST', '/records/application/a1/transitions/submit', ['input' => null])->assertOk();
    $this->host->settle();
    $this->signIn('admin');
    $this->api('GET', '/records/application/a1/history')->assertOk();
    $token = $this->api('GET', '/records/application/a1/reset')->assertOk()->json('token');
    $this->api('POST', '/records/application/a1/reset', ['input' => (object) [], 'token' => $token])->assertOk();
    $this->api('POST', '/records/application/a1/force-state', ['state' => 'submitted', 'reason' => 'Fix', 'skipSteps' => true])->assertOk();
    $this->api('POST', '/simulate', ['definition' => $lifecycle, 'request' => ['entityId' => 'a1', 'fromState' => 'draft', 'transition' => 'submit']])->assertOk();

    // Tasks
    $this->signIn('ali');
    $this->api('POST', '/records/application/a1/manual/sign', ['input' => null])->assertOk();
    $this->host->settle();
    $this->signIn('rev1');
    $task = $this->api('GET', '/tasks')->assertOk()->json('items.0.id');
    $this->api('GET', "/tasks/{$task}")->assertOk();
    $this->api('POST', "/tasks/{$task}/delegate", ['to' => 'rev2', 'note' => 'Yours'])->assertOk();
    $this->signIn('admin');
    $this->api('GET', '/tasks?scope=all&status=open,completed')->assertOk();
    $this->api('POST', "/tasks/{$task}/reassign", ['assignees' => ['rev1'], 'reason' => 'Back'])->assertOk();
    $this->signIn('rev1');
    $this->api('POST', "/tasks/{$task}/decide", ['outcome' => 'approved', 'comment' => null, 'data' => ['reference' => 'R-1']])->assertOk();

    // Signals and manual starts
    $this->signIn('admin');
    $this->api('POST', '/records/application/a1/manual/waitPay')->assertOk();
    $this->host->settle();
    $this->api('POST', '/signals/payment.received', ['entity' => 'application', 'entityId' => 'a1', 'data' => ['amount' => 7]])->assertOk();
    $this->api('POST', '/manual/alone', ['input' => null])->assertOk();

    // Instances and interventions
    $stuck = $this->api('POST', '/records/application/a1/manual/ping')->assertOk()->json('id');
    $this->host->settle();
    $this->api('GET', '/instances?status=incident')->assertOk();
    $this->api('GET', "/instances/{$stuck}")->assertOk();
    $this->api('GET', "/instances/{$stuck}/definition")->assertOk();
    $this->api('GET', "/instances/{$stuck}/audit?take=10")->assertOk();
    $this->api('POST', "/instances/{$stuck}/retry", ['target' => null])->assertOk();
    $this->api('POST', "/instances/{$stuck}/skip", ['outputs' => null, 'target' => null])->assertOk();
    $waiting = $this->api('POST', '/records/application/a1/manual/waitPay')->assertOk()->json('id');
    $this->api('POST', "/instances/{$waiting}/cancel", ['reason' => 'Not needed'])->assertOk();
    $record = $this->api('GET', '/instances?entity=application&entityId=a1&kind=record')->assertOk()->json('items.0.id');
    $this->api('POST', "/instances/{$record}/migrate", ['version' => 3, 'stateMap' => null])->assertOk();

    // Audit
    $this->api('GET', '/audit?take=5')->assertOk();
    $this->api('GET', '/audit/export?format=csv')->assertOk();
    $this->api('GET', '/audit/export')->assertOk();
    $this->api('POST', '/audit/verify')->assertOk();
    $this->api('POST', '/audit/purge')->assertOk();

    $missing = array_values(array_filter(OpenApiContract::operations(),
        fn (string $op) => ! array_any(array_keys(OpenApiContract::$seen[$op] ?? []), fn ($status) => $status < 300)));
    expect($missing)->toBe([]);
});
