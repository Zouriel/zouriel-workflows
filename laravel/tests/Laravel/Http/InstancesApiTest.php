<?php

use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

beforeEach(function () {
    $this->publishDefinition(Definitions::lifecycle());
    $this->publishDefinition(Definitions::ping());
    $this->publishDefinition(Definitions::waitForPayment());
    $this->record('a1', ['title' => 'Grant', 'applicant' => 'ali', 'amount' => 500]);
    $this->signIn('admin');
});

/** Starts the ping automation, which stops in an incident while crm.ping keeps failing. */
function stuckPing($test, int $failures): string
{
    $test->host->actions->set('crm.ping', failTimes: $failures);
    $id = $test->api('POST', '/records/application/a1/manual/ping')->assertOk()->json('id');
    $test->host->settle();

    return $id;
}

it('lists instances by filter, stuck ones included', function () {
    $stuck = stuckPing($this, 5);

    $all = $this->api('GET', '/instances')->assertOk()->json();
    expect(array_column($all['items'], 'kind'))->each->toBeIn(['automation', 'record'])
        ->and($all['total'])->toBe(count($all['items']));
    $incidents = $this->api('GET', '/instances?status=incident')->assertOk()->json();
    expect(array_column($incidents['items'], 'id'))->toBe([$stuck])
        ->and($incidents['items'][0])->toMatchArray(['status' => 'incident', 'errorStepId' => 'p', 'steps' => null])
        ->and($this->api('GET', '/instances?definitionKey=applicationReview')->json('items.0.kind'))->toBe('record')
        ->and($this->api('GET', '/instances?kind=automation&take=1&skip=0')->json('total'))->toBe(1)
        ->and($this->api('GET', '/instances?entityId=none')->json())->toBe(['items' => [], 'total' => 0]);
});

it('reads one instance with its steps, the version it runs and its audit', function () {
    $stuck = stuckPing($this, 5);

    $view = $this->api('GET', "/instances/{$stuck}")->assertOk()->json();
    expect($view['steps'])->toBeArray()->not->toBeEmpty()
        ->and($view['tokens'])->toBeArray()
        ->and($this->api('GET', "/instances/{$stuck}/definition")->assertOk()->json('key'))->toBe('ping')
        ->and(array_column($this->api('GET', "/instances/{$stuck}/audit")->assertOk()->json(), 'instanceId'))->each->toBe($stuck)
        ->and($this->api('GET', "/instances/{$stuck}/audit?take=1")->json())->toHaveCount(1);
    expect($this->api('GET', '/instances/nope')->assertNotFound()->json('code'))->toBe('notFound');
});

it('retries a failed step and skips past it', function () {
    $stuck = stuckPing($this, 5);

    expect($this->api('POST', "/instances/{$stuck}/retry")->assertOk()->json('status'))->toBe('incident');
    $skipped = $this->api('POST', "/instances/{$stuck}/skip", ['outputs' => ['ok' => true], 'target' => 'p'])->assertOk();
    $this->host->settle();
    expect($skipped->json('status'))->toBe('completed')
        ->and($this->host->store->peekRecord('application', 'a1')->get('priority'))->toBe('high')
        ->and($this->api('POST', "/instances/{$stuck}/retry")->assertStatus(409)->json('code'))->toBe('notInIncident');
});

it('cancels a running instance once', function () {
    $waiting = $this->api('POST', '/records/application/a1/manual/waitPay')->assertOk()->json('id');

    expect($this->api('POST', "/instances/{$waiting}/cancel", ['reason' => 'Not needed'])->assertOk()->json('status'))->toBe('cancelled')
        ->and($this->api('POST', "/instances/{$waiting}/cancel")->assertStatus(409)->json('code'))->toBe('finished');
});

it('moves a record instance to another version', function () {
    $this->api('PUT', '/definitions/lc_app', [...Definitions::lifecycle(), 'title' => 'Review v2'])->assertOk();
    $this->api('POST', '/definitions/lc_app/publish')->assertOk();
    $record = $this->api('GET', '/instances?kind=record')->json('items.0.id');

    expect($this->api('POST', "/instances/{$record}/migrate", ['version' => 2, 'stateMap' => ['draft' => 'draft']])->assertOk()->json('version'))->toBe(2)
        ->and($this->api('POST', "/instances/{$record}/migrate", ['version' => 7])->assertNotFound()->json('code'))->toBe('definitionNotFound')
        ->and($this->api('POST', "/instances/{$record}/migrate", ['version' => 1, 'stateMap' => ['draft' => 5]])->assertStatus(400)->json('code'))->toBe('invalidJson');
});
