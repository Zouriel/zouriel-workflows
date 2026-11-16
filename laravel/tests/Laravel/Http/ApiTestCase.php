<?php

namespace Zouriel\Workflows\Tests\Laravel\Http;

use Illuminate\Auth\GenericUser;
use Illuminate\Testing\TestResponse;
use Orchestra\Testbench\TestCase;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;
use Zouriel\Workflows\Laravel\Http\ActorResolver;
use Zouriel\Workflows\Laravel\Http\AuthActorResolver;
use Zouriel\Workflows\Laravel\WorkflowsServiceProvider;

/**
 * An app with the workflow API mounted, serving an engine from the container: here the test host's (in-memory store,
 * manual clock, recording fakes, the shared spec registry and users), but any store bound as `WorkflowEngine` works
 * the same. Every response is checked against spec/api/openapi.json.
 */
abstract class ApiTestCase extends TestCase
{
    public WorkflowTestHost $host;

    protected function getPackageProviders($app): array
    {
        return [WorkflowsServiceProvider::class];
    }

    protected function defineEnvironment($app): void
    {
        $this->host = engineHost();
        $app->instance(WorkflowEngine::class, $this->host->engine);
        $app->instance(ActorResolver::class, new AuthActorResolver(roles: fn (GenericUser $user) => $user->roles ?? []));
    }

    /** Creates an application record in the app's store and lets the engine react to it, as the worker would. */
    protected function record(string $id, array $values): void
    {
        $this->host->store->createRecord('application', $id, (object) $values);
        $this->host->settle();
    }

    /** Signs in as one of spec/fixtures/engine/_users.json (their roles come with them). */
    protected function signIn(string $userId): static
    {
        $user = $this->host->identity->user($userId);

        return $this->actingAs(new GenericUser(['id' => $user->id, 'name' => $user->name, 'roles' => $user->roles]));
    }

    /**
     * Calls the API with a JSON body (arrays and objects are encoded, a string is sent as it is) and checks the response
     * against the spec.
     */
    public function api(string $method, string $path, mixed $body = null): TestResponse
    {
        $uri = '/api/workflows'.$path;
        $content = match (true) {
            $body === null => '',
            is_string($body) => $body,
            default => json_encode($body, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES),
        };
        $response = $this->call($method, $uri, [], [], [], ['CONTENT_TYPE' => 'application/json', 'HTTP_ACCEPT' => 'application/json'], $content);
        OpenApiContract::check($method, $uri, $response);

        return $response;
    }

    /** The response body as engine values (objects as stdClass, numbers as floats). */
    protected static function body(TestResponse $response): mixed
    {
        return Json::decode($response->getContent());
    }

    /** Publishes a definition through the API as admin, failing the test when it is refused. */
    protected function publishDefinition(array|string $definition): void
    {
        $definition = is_string($definition) ? json_decode($definition, true) : $definition;
        $this->signIn('admin');
        $this->api('POST', '/definitions', $definition)->assertOk();
        $this->api('POST', "/definitions/{$definition['id']}/publish", ['note' => 'First'])->assertOk();
    }
}
