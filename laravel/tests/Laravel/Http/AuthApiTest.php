<?php

use Illuminate\Auth\GenericUser;
use Illuminate\Support\Facades\Gate;
use Illuminate\Support\Facades\Route;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Laravel\Http\AccessPolicy;
use Zouriel\Workflows\Laravel\Http\RecordAccessPolicy;
use Zouriel\Workflows\Laravel\Http\WorkflowApi;
use Zouriel\Workflows\Tests\Laravel\Http\Definitions;
use Zouriel\Workflows\Tests\Laravel\Http\OpenApiContract;

it('answers 401 everywhere to callers who are not signed in', function () {
    foreach (OpenApiContract::operations() as $operation) {
        [$method, $path] = explode(' ', $operation);
        $response = $this->api($method, preg_replace('/\{[^}]+\}/', '1', substr($path, strlen('/api/workflows'))));

        expect($response->getStatusCode())->toBe(401, $operation)
            ->and($response->json('code'))->toBe('unauthenticated');
    }
});

it('counts a signed-in principal without a user id as not signed in', function () {
    $this->actingAs(new GenericUser(['id' => '', 'name' => 'Nobody']));

    $this->api('GET', '/me')->assertUnauthorized();
});

it('lets anonymous callers in only when the API allows them, as a non-person', function () {
    config(['workflows.api.allow_anonymous' => true]);
    $this->publishDefinition(Definitions::standalone());
    $this->app['auth']->forgetGuards();

    expect($this->api('GET', '/me')->assertOk()->json())->toMatchArray(['type' => 'anonymous', 'id' => null, 'roles' => []])
        ->and($this->api('POST', '/manual/alone')->assertForbidden()->json('code'))->toBe('notAllowed');
});

it('mounts the API behind the configured middleware and path', function () {
    Route::middleware([])->group(fn () => WorkflowApi::routes('elsewhere/wf', ['auth']));
    Route::getRoutes()->refreshNameLookups();

    $this->getJson('/elsewhere/wf/me')->assertUnauthorized()->assertJson(['message' => 'Unauthenticated.']);
    $this->signIn('rev1');
    $this->getJson('/elsewhere/wf/me')->assertOk()->assertJsonPath('id', 'rev1');
});

dataset('gated operations', [
    'edit: create a draft' => ['edit', 'POST', '/definitions', Definitions::standalone()],
    'edit: read settings' => ['edit', 'GET', '/settings', null],
    'publish: archive' => ['publish', 'POST', '/definitions/wf_alone/archive', null],
    'publish: delete' => ['publish', 'DELETE', '/definitions/wf_alone', null],
    'intervene: cancel' => ['intervene', 'POST', '/instances/nothing/cancel', null],
    'intervene: force state' => ['intervene', 'POST', '/records/application/a1/force-state', ['state' => 'draft', 'reason' => 'x']],
    'intervene: reassign' => ['intervene', 'POST', '/tasks/nothing/reassign', ['assignees' => ['rev1']]],
    'audit: read' => ['audit', 'GET', '/audit', null],
    'audit: verify' => ['audit', 'POST', '/audit/verify', null],
    'audit: an instance\'s entries' => ['audit', 'GET', '/instances/nothing/audit', null],
    'audit_export: export' => ['audit_export', 'GET', '/audit/export', null],
    'settings: save settings' => ['settings', 'PUT', '/settings', ['settings' => ['version' => 1], 'revision' => 0]],
    'settings: purge' => ['settings', 'POST', '/audit/purge', null],
    'view: read definitions' => ['view', 'GET', '/definitions', null],
    'signal: raise one' => ['signal', 'POST', '/signals/nightly', null],
]);

it('refuses an operation whose gate denies the caller, and lets those it allows through', function (string $kind, string $method, string $path, ?array $body) {
    Gate::define('admins', fn ($user) => in_array('admin', $user->roles ?? [], true));
    config(["workflows.api.gates.{$kind}" => 'admins']);

    $this->signIn('rev1');
    expect($this->api($method, $path, $body)->assertForbidden()->json('code'))->toBe('notAllowed');

    $this->signIn('admin');
    expect($this->api($method, $path, $body)->getStatusCode())->not->toBe(403);
})->with('gated operations');

it('exports with the audit gate when no export gate is set', function () {
    Gate::define('admins', fn ($user) => in_array('admin', $user->roles ?? [], true));
    config(['workflows.api.gates.audit' => 'admins']);

    $this->signIn('rev1');
    $this->api('GET', '/audit/export')->assertForbidden();
    $this->signIn('admin');
    $this->api('GET', '/audit/export')->assertOk();
});

it('refuses signals to everyone without a signal or intervention gate, and uses the intervention gate when only it is set', function () {
    $this->signIn('admin');
    expect($this->api('POST', '/signals/nightly')->assertForbidden()->json('message'))->toBe('Signals cannot be raised through the API here.');

    Gate::define('admins', fn ($user) => in_array('admin', $user->roles ?? [], true));
    config(['workflows.api.gates.intervene' => 'admins']);
    expect($this->api('POST', '/signals/nightly')->assertOk()->json())->toBe(['resumed' => 0]);
    $this->signIn('rev1');
    $this->api('POST', '/signals/nightly')->assertForbidden();
});

it('needs the intervention gate for everyone\'s tasks', function () {
    Gate::define('admins', fn ($user) => in_array('admin', $user->roles ?? [], true));
    config(['workflows.api.gates.intervene' => 'admins']);

    $this->signIn('rev1');
    $this->api('GET', '/tasks?scope=all')->assertForbidden();
    $this->api('GET', '/tasks')->assertOk();
    $this->signIn('admin');
    $this->api('GET', '/tasks?scope=all')->assertOk();
});

it('hides records the access policy refuses, with their instances, tasks and history', function () {
    $this->app->instance(AccessPolicy::class, new class extends RecordAccessPolicy
    {
        public function canViewRecord(WorkflowActor $actor, string $entity, string $id): bool
        {
            return $id !== 'secret' || $actor->id === 'admin';
        }
    });
    $this->publishDefinition(Definitions::lifecycle());
    $this->publishDefinition(Definitions::signOff());
    $this->record('open', ['title' => 'Open', 'applicant' => 'ali']);
    $this->record('secret', ['title' => 'Secret', 'applicant' => 'ali']);
    $this->signIn('admin');
    $this->api('POST', '/records/application/secret/manual/sign')->assertOk();
    $this->api('POST', '/records/application/open/manual/sign')->assertOk();
    $this->host->settle();

    $this->signIn('rev2');
    $this->api('GET', '/records/application/open')->assertOk();
    foreach (['GET /records/application/secret', 'GET /records/application/secret/history', 'GET /records/application/secret/reset',
        'POST /records/application/secret/transitions/withdraw', 'POST /records/application/secret/manual/sign'] as $call) {
        [$method, $path] = explode(' ', $call);
        expect($this->api($method, $path)->assertForbidden()->json('message'))->toBe('You cannot see this record.');
    }
    $instances = $this->api('GET', '/instances?entity=application')->assertOk()->json();
    expect(array_unique(array_column($instances['items'], 'entityId')))->toBe(['open'])
        ->and($instances['total'])->toBe(count($instances['items']));
    $secretInstance = $this->host->engine->queryInstances(new InstanceQuery(entityId: 'secret', kinds: ['automation']))->items[0]->id;
    $this->api('GET', "/instances/{$secretInstance}")->assertForbidden();
    $this->api('POST', "/instances/{$secretInstance}/cancel")->assertForbidden();
    $this->api('POST', '/simulate', ['definition' => Definitions::lifecycle(), 'request' => ['entityId' => 'secret']])->assertForbidden();

    // The assignee still sees the task about the hidden record; others do not.
    $this->signIn('rev1');
    $task = $this->api('GET', '/tasks?entityId=secret')->assertOk()->json('items.0.id');
    $this->api('GET', "/tasks/{$task}")->assertOk();
    $this->signIn('rev2');
    $this->api('GET', "/tasks/{$task}")->assertForbidden();
    expect($this->api('GET', '/tasks?scope=all')->assertOk()->json('total'))->toBe(1);

    $this->signIn('admin');
    $this->api('GET', '/records/application/secret')->assertOk();
    expect($this->api('GET', '/tasks?scope=all')->json('total'))->toBe(2);
});
