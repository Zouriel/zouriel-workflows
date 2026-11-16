<?php

use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

beforeEach(fn () => $this->signIn('admin'));

it('creates, lists, reads and saves drafts', function () {
    $definition = Definitions::standalone();

    $created = $this->api('POST', '/definitions', $definition)->assertOk()->json();
    expect($created)->toMatchArray(['id' => 'wf_alone', 'key' => 'alone', 'kind' => 'automation', 'status' => 'draft', 'version' => 0, 'entity' => null,
        'updatedBy' => 'admin', 'runningInstances' => 0])
        ->and($created['updatedAt'])->toMatch('/^2026-10-19T09:00:00\+00:00$/');

    $saved = $this->api('PUT', '/definitions/wf_alone', [...$definition, 'title' => 'Alone again'])->assertOk();
    expect($saved->json('title'))->toBe('Alone again')
        ->and(array_column($this->api('GET', '/definitions')->assertOk()->json(), 'id'))->toBe(['wf_alone']);

    $detail = $this->api('GET', '/definitions/wf_alone')->assertOk()->json();
    expect(array_keys($detail))->toBe(['summary', 'draft', 'published', 'versions', 'problems'])
        ->and($detail['draft']['title'])->toBe('Alone again')
        ->and($detail['published'])->toBeNull()
        ->and($detail['versions'])->toBe([]);
});

it('refuses a second workflow with the same id or key', function () {
    $this->api('POST', '/definitions', Definitions::standalone())->assertOk();

    expect($this->api('POST', '/definitions', Definitions::standalone())->assertStatus(409)->json('code'))->toBe('duplicateId')
        ->and($this->api('POST', '/definitions', [...Definitions::standalone(), 'id' => 'wf_other'])->assertStatus(409)->json('code'))->toBe('duplicateKey');
});

it('answers bodies that are not JSON, not a definition, or too large with the standard error', function () {
    $notJson = $this->api('POST', '/definitions', '{"id": ')->assertStatus(400)->json();
    expect($notJson['code'])->toBe('invalidJson')
        ->and($notJson)->not->toHaveKey('details');

    expect($this->api('POST', '/definitions', '[1, 2]')->assertStatus(400)->json('code'))->toBe('invalidJson')
        ->and($this->api('POST', '/definitions/wf_alone/rollback', ['version' => 'two'])->assertStatus(400)->json('code'))->toBe('invalidJson')
        ->and($this->api('POST', '/definitions/wf_alone/rollback')->assertStatus(400)->json('message'))->toBe('The request needs a JSON body.');

    config(['workflows.api.max_body_bytes' => 100]);
    $tooLarge = $this->api('POST', '/definitions', Definitions::standalone())->assertStatus(413)->json();
    expect($tooLarge['code'])->toBe('tooLarge');
});

it('answers 404 for a workflow or version that does not exist', function () {
    expect($this->api('GET', '/definitions/nope')->assertNotFound()->json('code'))->toBe('notFound');
    $this->publishDefinition(Definitions::standalone());
    expect($this->api('GET', '/definitions/wf_alone/versions/9')->assertNotFound()->json('code'))->toBe('notFound');
});

it('refuses to publish a draft with lint errors and lists them, leaving out nodes they are not on', function () {
    $broken = [...Definitions::standalone(), 'steps' => [['id' => 'n', 'type' => 'notify', 'channel' => 'pigeon', 'to' => [['user' => 'admin']], 'body' => 'x']]];
    $this->api('POST', '/definitions', $broken)->assertOk();

    $refused = $this->api('POST', '/definitions/wf_alone/publish', ['note' => null])->assertStatus(400)->json();

    expect($refused['code'])->toBe('lintErrors')
        ->and($refused['details'])->not->toBeEmpty()
        ->and($refused['details'][0])->toHaveKeys(['code', 'severity', 'message']);
    $this->api('POST', '/validate', $broken)->assertOk()->assertJsonPath('0.code', $refused['details'][0]['code']);
});

it('publishes, keeps versions, diffs the draft, rolls back, archives, restores and deletes drafts only', function () {
    $this->publishDefinition(Definitions::standalone());
    $this->api('PUT', '/definitions/wf_alone', [...Definitions::standalone(), 'title' => 'Renamed'])->assertOk();

    expect($this->api('GET', '/definitions/wf_alone/diff')->assertOk()->json())->not->toBeEmpty()
        ->and($this->api('POST', '/definitions/wf_alone/publish', ['note' => 'Second'])->assertOk()->json('version'))->toBe(2)
        ->and($this->api('GET', '/definitions/wf_alone/versions/1')->assertOk()->json('title'))->toBe('Alone')
        ->and(array_column($this->api('GET', '/definitions/wf_alone')->json('versions'), 'note'))->toBe(['Second', 'First'])
        ->and($this->api('GET', '/definitions/wf_alone')->json('versions.0.publishedAt'))->toBe('2026-10-19T09:00:00+00:00')
        ->and($this->api('POST', '/definitions/wf_alone/rollback', ['version' => 1])->assertOk()->json('version'))->toBe(3)
        ->and($this->api('GET', '/definitions/wf_alone/versions/3')->json('title'))->toBe('Alone')
        ->and($this->api('POST', '/definitions/wf_alone/archive')->assertOk()->json('status'))->toBe('archived')
        ->and($this->api('POST', '/definitions/wf_alone/restore')->assertOk()->json('status'))->toBe('published')
        ->and($this->api('DELETE', '/definitions/wf_alone')->assertStatus(409)->json('code'))->toBe('published');

    $this->api('POST', '/definitions', [...Definitions::standalone(), 'id' => 'wf_draft', 'key' => 'draft'])->assertOk();
    $this->api('DELETE', '/definitions/wf_draft')->assertNoContent()->assertContent('');
    $this->api('GET', '/definitions/wf_draft')->assertNotFound();
});

it('changes who may do a node and publishes only that change', function () {
    $this->publishDefinition(Definitions::lifecycle());

    $changed = $this->api('POST', '/definitions/lc_app/permissions', ['changes' => [['node' => 'tr_no', 'who' => [['role' => 'admin']]]], 'note' => 'Admins reject'])
        ->assertOk()->json();

    expect(array_slice(array_keys($changed), 0, 4))->toBe(['changed', 'draftSkipped', 'recordsMoved', 'recordsKept'])
        ->and($changed)->toMatchArray(['changed' => true, 'id' => 'lc_app', 'version' => 2])
        ->and($this->api('GET', '/definitions/lc_app/versions/2')->json('lifecycle.transitions.3.who'))->toBe([['role' => 'admin']])
        ->and($this->api('POST', '/definitions/lc_app/permissions', ['changes' => []])->assertStatus(400)->json('code'))->toBe('invalidInput')
        ->and($this->api('POST', '/definitions/lc_app/permissions', ['changes' => [['node' => 'nope', 'who' => []]]])->assertStatus(400)->json('code'))->toBe('unknownNode');
});
