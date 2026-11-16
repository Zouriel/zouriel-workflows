<?php

use Illuminate\Support\Facades\Gate;
use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

it('lists the registry and the step types', function () {
    $this->signIn('rev1');

    $schema = $this->api('GET', '/schema')->assertOk();

    expect($schema->json('specVersion'))->toBe(1)
        ->and(array_column($schema->json('registry.entities'), 'key'))->toContain('application')
        ->and(array_column($schema->json('steps'), 'type'))->toContain('approval', 'setFields')
        ->and($schema->json('registry.departments'))->toBeNull();
});

it('says who is asking and what their gates let them do', function () {
    Gate::define('wf-edit', fn ($user) => in_array('admin', $user->roles ?? [], true));
    config(['workflows.api.gates.edit' => 'wf-edit', 'workflows.api.gates.audit' => 'wf-edit']);

    $this->signIn('rev1');
    $me = $this->api('GET', '/me')->assertOk();
    expect($me->json())->toBe(['type' => 'user', 'id' => 'rev1', 'name' => 'Rana Reviewer', 'roles' => ['reviewer'],
        'can' => ['edit' => false, 'publish' => true, 'settings' => true, 'intervene' => true, 'audit' => false]]);

    $this->signIn('admin');
    expect($this->api('GET', '/me')->json('can'))->toBe(['edit' => true, 'publish' => true, 'settings' => true, 'intervene' => true, 'audit' => true]);
});

it('searches the directory and reads one person, with an empty 404 for someone unknown', function () {
    $this->signIn('admin');

    expect(array_column($this->api('GET', '/directory/users?q=Re')->assertOk()->json(), 'id'))->toContain('rev1', 'rev2', 'rev3')
        ->and($this->api('GET', '/directory/users?q=Re&take=1')->json())->toHaveCount(1)
        ->and($this->api('GET', '/directory/users/rev1')->assertOk()->json())->toMatchArray(['id' => 'rev1', 'email' => 'rana@example.com', 'roles' => ['reviewer']]);
    $this->api('GET', '/directory/users/nobody')->assertNotFound()->assertContent('');
});

it('reads and saves global settings by revision', function () {
    $this->signIn('admin');

    $settings = $this->api('GET', '/settings')->assertOk();
    expect($settings->json('revision'))->toBe(0);

    $saved = $this->api('PUT', '/settings', ['settings' => ['version' => 1, 'timeZone' => 'Indian/Maldives'], 'revision' => 0])->assertOk();
    expect($saved->json())->toBe(['revision' => 1])
        ->and($this->api('GET', '/settings')->json('settings.timeZone'))->toBe('Indian/Maldives');
});

it('refuses settings that fail their checks and saves based on an old revision', function () {
    $this->signIn('admin');

    $invalid = $this->api('PUT', '/settings', ['settings' => ['version' => 1, 'timeZone' => 'Mars/Olympus'], 'revision' => 0])->assertStatus(400);
    expect($invalid->json('code'))->toBe('invalidSettings')
        ->and($invalid->json('details'))->toBeArray()->not->toBeEmpty();

    $this->api('PUT', '/settings', ['settings' => ['version' => 1], 'revision' => 0])->assertOk();
    expect($this->api('PUT', '/settings', ['settings' => ['version' => 1], 'revision' => 0])->assertStatus(409)->json('code'))->toBe('staleSettings');
});

it('lists what is being watched in plain words', function () {
    $this->publishDefinition(Definitions::lifecycle());

    $triggers = $this->api('GET', '/triggers')->assertOk()->json();

    expect($triggers)->toHaveCount(1)
        ->and($triggers[0])->toMatchArray(['definitionKey' => 'applicationReview', 'type' => 'lifecycle', 'summary' => 'Controls Application Status']);
});
