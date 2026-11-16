<?php

use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

beforeEach(function () {
    $lifecycle = Definitions::lifecycle();
    $lifecycle['lifecycle']['transitions'][3]['comment'] = 'required';
    $this->publishDefinition($lifecycle);
    $this->record('a1', ['title' => 'Grant', 'applicant' => 'ali', 'amount' => 500]);
});

it('shows a record with its state and what the caller may do', function () {
    $this->signIn('ali');

    $record = $this->api('GET', '/records/application/a1')->assertOk()->json();

    expect($record)->toMatchArray(['entity' => 'application', 'id' => 'a1', 'title' => 'Grant', 'lifecycle' => 'applicationReview', 'state' => 'draft',
        'stateLabel' => 'Draft', 'reset' => null])
        ->and(array_column($record['transitions'], 'key'))->toBe(['submit', 'withdraw']);
    expect($this->api('GET', '/records/application/nope')->assertNotFound()->json('code'))->toBe('noRecord');
});

it('moves a record through its lifecycle with forms and remarks, newest history first', function () {
    $this->signIn('ali');
    expect($this->api('POST', '/records/application/a1/transitions/submit')->assertOk()->json('state'))->toBe('submitted');
    $this->host->settle();
    $this->signIn('rev1');
    $this->api('POST', '/records/application/a1/transitions/startReview', ['input' => null])->assertOk()->assertJsonPath('state', 'underReview');

    $missing = $this->api('POST', '/records/application/a1/transitions/approve', ['input' => (object) []])->assertStatus(400)->json();
    expect($missing['code'])->toBe('invalidInput')
        ->and(json_encode($missing['details']))->toContain('comment');
    expect($this->api('POST', '/records/application/a1/transitions/reject', ['input' => null])->assertStatus(400)->json('code'))->toBe('commentRequired');

    $approved = $this->api('POST', '/records/application/a1/transitions/approve', ['input' => ['comment' => 'Fine']])->assertOk();
    expect($approved->json('state'))->toBe('approved')
        ->and($this->host->store->peekRecord('application', 'a1')->get('notes'))->toBe('Fine');

    $history = $this->api('GET', '/records/application/a1/history')->assertOk()->json();
    $newestFirst = array_column($history, 'seq');
    rsort($newestFirst);
    expect(array_column($history, 'seq'))->toBe($newestFirst)
        ->and(array_column($history, 'action'))->toContain('transitioned')
        ->and(array_column($history, 'category'))->each->toBe('lifecycle')
        ->and($this->api('GET', '/records/application/a1/history?take=1')->json())->toHaveCount(1)
        ->and($history[0]['at'])->toEndWith('+00:00');
});

it('refuses moves the caller may not make, that do not exist, or not from this state', function () {
    $this->signIn('rev1');
    expect($this->api('POST', '/records/application/a1/transitions/submit')->assertForbidden()->json('code'))->toBe('notAllowed')
        ->and($this->api('POST', '/records/application/a1/transitions/fly')->assertStatus(400)->json('code'))->toBe('unknownTransition')
        ->and($this->api('POST', '/records/application/a1/transitions/approve', ['input' => ['comment' => 'x']])->assertStatus(400)->json('code'))
        ->toBe('notInFromState');
});

it('previews a reset for the people it allows and refuses one whose list changed since', function () {
    $this->signIn('ali');
    $this->api('POST', '/records/application/a1/transitions/submit')->assertOk();
    $this->host->settle();

    expect($this->api('GET', '/records/application/a1/reset')->assertForbidden()->json('code'))->toBe('notAllowed');
    $this->signIn('admin');
    $preview = $this->api('GET', '/records/application/a1/reset')->assertOk()->json();
    expect($preview['reset'])->toMatchArray(['label' => 'Start over', 'to' => 'draft'])
        ->and($preview['token'])->toBeString();

    expect($this->api('POST', '/records/application/a1/reset', ['token' => 'stale'])->assertStatus(409)->json('code'))->toBe('resetChanged');
    $reset = $this->api('POST', '/records/application/a1/reset', ['input' => (object) [], 'token' => $preview['token']])->assertOk();
    $this->host->settle();
    expect($reset->json('state'))->toBe('draft')
        ->and($this->host->store->peekRecord('application', 'a1')->get('notes'))->toBe('reset');
    expect($this->api('POST', '/records/application/a1/reset')->assertStatus(409)->json('code'))->toBe('nothingToReset');
});

it('forces a state with a reason', function () {
    $this->signIn('admin');

    expect($this->api('POST', '/records/application/a1/force-state', ['state' => 'approved', 'reason' => ''])->assertStatus(400)->json('code'))->toBe('reasonRequired');
    $this->api('POST', '/records/application/a1/force-state', ['state' => 'approved', 'reason' => 'Paper file', 'skipSteps' => true, 'lifecycle' => null])
        ->assertOk()->assertJsonPath('state', 'approved');
    expect($this->api('POST', '/records/application/a1/force-state', ['state' => 'nowhere', 'reason' => 'x'])->assertStatus(400)->json('code'))->toBe('unknownState');
});

it('starts manual workflows on a record and on none', function () {
    $this->publishDefinition(Definitions::standalone());
    $this->publishDefinition(Definitions::waitForPayment());
    $this->signIn('rev1');

    $started = $this->api('POST', '/records/application/a1/manual/waitPay', ['input' => null])->assertOk()->json();
    expect($started)->toMatchArray(['definitionKey' => 'waitPay', 'entity' => 'application', 'entityId' => 'a1', 'initiatorId' => 'rev1']);
    expect($this->api('POST', '/manual/alone')->assertOk()->json('entity'))->toBeNull()
        ->and($this->api('POST', '/manual/nothing')->assertStatus(400)->json('code'))->toBe('unknownWorkflow');
});
