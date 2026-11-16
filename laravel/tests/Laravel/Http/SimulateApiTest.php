<?php

use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

beforeEach(fn () => $this->signIn('admin'));

it('validates a definition without saving it', function () {
    expect($this->api('POST', '/validate', Definitions::standalone())->assertOk()->json())->toBe([]);

    $problems = $this->api('POST', '/validate', [...Definitions::standalone(), 'steps' => [['id' => 'n', 'type' => 'notify', 'channel' => 'pigeon',
        'to' => [['user' => 'admin']], 'body' => 'x']]])->assertOk()->json();
    expect($problems)->not->toBeEmpty()
        ->and($problems[0])->toHaveKeys(['code', 'severity', 'node', 'message'])
        ->and($this->api('GET', '/definitions')->json())->toBe([]);
});

it('runs a test of a lifecycle from a real record without changing it', function () {
    $this->record('a1', ['title' => 'Grant', 'applicant' => 'ali', 'amount' => 500]);

    $run = $this->api('POST', '/simulate', ['definition' => Definitions::lifecycle(), 'request' => [
        'entityId' => 'a1', 'fromState' => 'draft', 'transition' => 'submit', 'input' => null, 'events' => [['type' => 'advance', 'duration' => 'PT1H']],
    ]])->assertOk()->json();

    expect(array_keys($run))->toBe(['problems', 'log', 'instances', 'openTasks', 'record', 'error'])
        ->and($run['error'])->toBeNull()
        ->and($run['record']['stage'])->toBe('checked')
        ->and($this->host->store->peekRecord('application', 'a1')->get('stage'))->toBeNull();
});

it('refuses test runs with too many events or a duration out of range', function () {
    $events = array_fill(0, 51, ['type' => 'advance', 'duration' => 'PT1M']);
    $tooMany = $this->api('POST', '/simulate', ['definition' => Definitions::standalone(), 'request' => ['events' => $events]])->assertStatus(400)->json();
    expect($tooMany)->toBe(['code' => 'tooManyEvents', 'message' => 'A test run can replay at most 50 events.']);

    config(['workflows.api.max_simulation_events' => 2]);
    expect($this->api('POST', '/simulate', ['definition' => Definitions::standalone(), 'request' => ['events' => array_slice($events, 0, 3)]])
        ->assertStatus(400)->json('message'))->toBe('A test run can replay at most 2 events.')
        ->and($this->api('POST', '/simulate', ['definition' => Definitions::standalone(), 'request' => ['events' => [['type' => 'advance', 'duration' => 'P999Y']]]])
            ->assertStatus(400)->json('code'))->toBe('badDuration')
        ->and($this->api('POST', '/simulate', ['definition' => Definitions::standalone(), 'request' => ['events' => [['type' => 'advance', 'duration' => 'PT1M']]]])
            ->assertOk()->json('error'))->toBeNull();
});

it('reports a definition that is not one as problems, and refuses a request of the wrong shape', function () {
    expect($this->api('POST', '/simulate', ['definition' => null])->assertOk()->json('problems'))->not->toBeEmpty()
        ->and($this->api('POST', '/simulate', ['definition' => Definitions::standalone(), 'request' => ['events' => 'many']])->assertStatus(400)->json('code'))
        ->toBe('invalidJson');
});
