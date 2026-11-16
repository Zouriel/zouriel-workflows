<?php

use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

beforeEach(function () {
    $this->publishDefinition(Definitions::signOff());
    $this->record('a1', ['title' => 'Grant', 'applicant' => 'ali', 'amount' => 500]);
    $this->signIn('head');
    $this->api('POST', '/records/application/a1/manual/sign')->assertOk();
    $this->host->settle();
    $this->task = $this->host->engine->queryTasks(new TaskQuery, null)->items[0]->id;
});

it('lists the caller\'s open tasks, or everyone\'s for interveners', function () {
    $this->signIn('rev1');
    $mine = $this->api('GET', '/tasks')->assertOk()->json();
    expect($mine['total'])->toBe(1)
        ->and($mine['items'][0])->toMatchArray(['id' => $this->task, 'kind' => 'approval', 'title' => 'Sign it', 'assignees' => ['rev1', 'ali'],
            'assigneeNames' => ['Rana Reviewer', 'Ali Hassan'], 'canDecide' => true, 'allowDelegation' => true, 'requireComment' => 'reject'])
        ->and($mine['items'][0]['form']['id'])->toBe('f_sign');

    $this->signIn('rev2');
    expect($this->api('GET', '/tasks')->json('total'))->toBe(0)
        ->and($this->api('GET', '/tasks?scope=all')->assertOk()->json('total'))->toBe(1)
        ->and($this->api('GET', '/tasks?scope=all&status=completed')->json('total'))->toBe(0)
        ->and($this->api('GET', '/tasks?scope=all&entity=application&entityId=other')->json('total'))->toBe(0)
        ->and($this->api('GET', "/tasks/{$this->task}")->assertOk()->json('canDecide'))->toBeFalse();
    expect($this->api('GET', '/tasks/nope')->assertNotFound()->json('code'))->toBe('notFound');
});

it('pages tasks with clamped skip and take', function () {
    $this->signIn('rev1');

    expect($this->api('GET', '/tasks?take=0')->assertOk()->json('items'))->toHaveCount(1)
        ->and($this->api('GET', '/tasks?skip=1')->json())->toBe(['items' => [], 'total' => 1])
        ->and($this->api('GET', '/tasks?take=many')->assertStatus(400)->json('code'))->toBe('invalidInput');
});

it('decides with the form\'s answers, refusing a missing answer, a missing reason to reject, and a second decision', function () {
    $this->signIn('rev1');

    $missing = $this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'approved', 'data' => (object) []])->assertStatus(400)->json();
    expect($missing['code'])->toBe('invalidInput')
        ->and(json_encode($missing['details']))->toContain('reference');
    expect($this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'rejected', 'data' => ['reference' => 'R']])->assertStatus(400)->json('code'))
        ->toBe('commentRequired')
        ->and($this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'maybe'])->assertStatus(400)->json('code'))->toBe('invalidOutcome');

    $decided = $this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'approved', 'comment' => ' Looks right ', 'data' => ['reference' => 'R-7']])
        ->assertOk()->json();
    expect($decided)->toMatchArray(['status' => 'completed', 'outcome' => 'approved', 'canDecide' => false])
        ->and($decided['decisions'][0])->toMatchArray(['userId' => 'rev1', 'outcome' => 'approved', 'comment' => 'Looks right', 'data' => ['reference' => 'R-7']]);
    $this->host->settle();
    expect($this->host->store->peekRecord('application', 'a1')->get('stage'))->toBe('done')
        ->and($this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'approved', 'data' => ['reference' => 'R']])->assertStatus(409)->json('code'))
        ->toBe('taskClosed')
        ->and(array_column($this->api('GET', '/records/application/a1/history')->json(), 'action'))->toContain('decided');
});

it('refuses the person who started the sign-off with segregationOfDuties', function () {
    $this->record('a2', ['title' => 'Second', 'applicant' => 'rev3']);
    $this->signIn('ali');
    $this->api('POST', '/records/application/a2/manual/sign')->assertOk();
    $this->host->settle();
    $own = $this->host->engine->queryTasks(new TaskQuery(statuses: ['open']), null)->items;
    $task = array_values(array_filter($own, fn ($t) => $t->id !== $this->task))[0];
    expect($task->assignees)->toBe(['rev1']);

    $refused = $this->api('POST', "/tasks/{$task->id}/decide", ['outcome' => 'approved', 'data' => ['reference' => 'R']])->assertForbidden()->json();
    expect($refused['code'])->toBe('segregationOfDuties')
        ->and($refused['message'])->toBe('You cannot approve this: the person who started it cannot approve it.');

    // On the sign-off head started, ali is an assignee and may decide.
    $this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'approved', 'data' => ['reference' => 'R']])->assertOk();
});

it('lets an assignee hand the task on and an intervener reassign it', function () {
    $this->signIn('rev2');
    expect($this->api('POST', "/tasks/{$this->task}/delegate", ['to' => 'rev3'])->assertForbidden()->json('code'))->toBe('notAllowed');

    $this->signIn('rev1');
    expect($this->api('POST', "/tasks/{$this->task}/delegate", ['to' => 'ali'])->assertStatus(409)->json('code'))->toBe('alreadyAssigned')
        ->and($this->api('POST', "/tasks/{$this->task}/delegate", ['to' => 'ghost'])->assertStatus(400)->json('code'))->toBe('unknownUser')
        ->and($this->api('POST', "/tasks/{$this->task}/delegate", ['to' => 'rev2', 'note' => 'Over to you'])->assertOk()->json('assignees'))->toBe(['rev2', 'ali']);

    $this->signIn('admin');
    expect($this->api('POST', "/tasks/{$this->task}/reassign", ['assignees' => []])->assertStatus(400)->json('code'))->toBe('noAssignees')
        ->and($this->api('POST', "/tasks/{$this->task}/reassign", ['assignees' => ['head']])->assertForbidden()->json('code'))->toBe('segregationOfDuties')
        ->and($this->api('POST', "/tasks/{$this->task}/reassign", ['assignees' => ['rev3'], 'reason' => 'Leave'])->assertOk()->json('assignees'))->toBe(['rev3']);
});

it('refuses decision answers over the configured size', function () {
    config(['workflows.api.max_decision_data_bytes' => 1024]);
    $this->signIn('rev1');

    $tooLarge = $this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'approved', 'data' => ['reference' => str_repeat('x', 2000)]])
        ->assertStatus(413)->json();
    expect($tooLarge)->toBe(['code' => 'tooLarge', 'message' => 'The answers are too large (at most 1 KB).']);
    $this->api('POST', "/tasks/{$this->task}/decide", ['outcome' => 'approved', 'data' => ['reference' => str_repeat('x', 900)]])->assertOk();
});
