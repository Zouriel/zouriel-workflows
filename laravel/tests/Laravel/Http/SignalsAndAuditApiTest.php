<?php

use Illuminate\Support\Facades\Gate;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Tests\Laravel\Http\Definitions;

beforeEach(function () {
    Gate::define('admins', fn ($user) => in_array('admin', $user->roles ?? [], true));
    config(['workflows.api.gates.signal' => 'admins']);
    $this->publishDefinition(Definitions::waitForPayment());
    $this->record('a1', ['title' => 'Grant', 'applicant' => 'ali', 'amount' => 500]);
});

it('resumes the workflows waiting for a signal about a record', function () {
    $this->api('POST', '/records/application/a1/manual/waitPay')->assertOk();
    $this->host->settle();

    expect($this->api('POST', '/signals/payment.received', ['entity' => 'application', 'entityId' => 'a2', 'data' => ['amount' => 7]])->assertOk()->json())
        ->toBe(['resumed' => 0])
        ->and($this->api('POST', '/signals/payment.received', ['Entity' => 'application', 'entityID' => 'a1', 'data' => ['amount' => 7]])->assertOk()->json())
        ->toBe(['resumed' => 1]);
    $this->host->settle();
    expect($this->host->store->peekRecord('application', 'a1')->get('amount'))->toBe(7.0)
        ->and($this->api('POST', '/signals/nope')->assertStatus(400)->json('code'))->toBe('unknownSignal')
        ->and($this->api('POST', '/signals/payment.received', ['entity' => 'application'])->assertNotFound()->json('code'))->toBe('noRecord');
});

it('queries the audit log by filter, newest first', function () {
    $this->api('POST', '/records/application/a1/manual/waitPay')->assertOk();
    $entries = $this->api('GET', '/audit')->assertOk()->json();
    expect(array_column($entries, 'seq'))->toBe(range(count($entries), 1))
        ->and(array_keys($entries[0]))->toBe(['seq', 'id', 'at', 'tenant', 'actorType', 'actorId', 'actorName', 'category', 'action', 'subjectType', 'subjectId',
            'entity', 'entityId', 'instanceId', 'definitionId', 'before', 'after', 'data', 'correlationId', 'causationId', 'prevHash', 'hash'])
        ->and($entries[0]['at'])->toBe('2026-10-19T09:00:00+00:00');

    expect(array_column($this->api('GET', '/audit?category=definition&action=published')->json(), 'definitionId'))->toBe(['wf_pay'])
        ->and($this->api('GET', '/audit?take=2')->json())->toHaveCount(2)
        ->and($this->api('GET', '/audit?before=3')->json('0.seq'))->toBe(2)
        ->and($this->api('GET', '/audit?actorId=admin&definitionId=wf_pay&category=definition')->json())->toHaveCount(2)
        ->and($this->api('GET', '/audit?from=2026-10-20T00:00:00Z')->json())->toBe([])
        ->and($this->api('GET', '/audit?to=2026-10-20')->json())->toHaveCount(count($entries))
        ->and($this->api('GET', '/audit?from=yesterday')->assertStatus(400)->json('code'))->toBe('invalidInput');
});

it('exports the audit log as CSV and as JSON, and audits the export', function () {
    $this->api('POST', '/records/application/a1/manual/waitPay')->assertOk();
    $count = count($this->host->store->all('audit'));

    $csv = $this->api('GET', '/audit/export?format=csv&category=definition')->assertOk();
    $lines = explode("\n", trim($csv->streamedContent()));
    expect($csv->headers->get('Content-Disposition'))->toBe('attachment; filename="workflow-audit.csv"')
        ->and($csv->headers->get('Content-Type'))->toStartWith('text/csv')
        ->and($lines[0])->toBe('seq,at,actorType,actorId,actorName,category,action,subjectType,subjectId,entity,entityId,instanceId,definitionId,before,after,data,correlationId,prevHash,hash')
        ->and($lines)->toHaveCount(3)
        ->and($lines[1])->toStartWith('1,"2026-10-19T09:00:00.000Z","user","admin","Admin","definition","created"')
        ->and($lines[1])->toContain(',"'.str_repeat('0', 64).'","');

    $json = $this->api('GET', '/audit/export')->assertOk();
    $records = json_decode($json->streamedContent(), true);
    expect($json->headers->get('Content-Disposition'))->toBe('attachment; filename="workflow-audit.json"')
        ->and($records)->toHaveCount($count + 1)
        ->and($records[0])->not->toHaveKey('tenant')
        ->and($records[0])->toMatchArray(['seq' => 1, 'at' => '2026-10-19T09:00:00+00:00']);

    $exports = $this->api('GET', '/audit?category=audit&action=exported')->json();
    expect($exports)->toHaveCount(2)
        ->and($count)->toBeGreaterThan(2)
        ->and(json_decode($exports[1]['data'], true))->toBe(['format' => 'csv', 'count' => 2, 'filter' => ['category' => 'definition', 'entity' => null, 'entityId' => null]]);
});

it('verifies the hash chain and finds an entry changed afterwards', function () {
    expect($this->api('POST', '/audit/verify')->assertOk()->json())->toBe(['valid' => true, 'checked' => count($this->host->store->all('audit')),
        'brokenAtSeq' => null, 'problem' => null, 'purgedGaps' => []])
        ->and($this->api('POST', '/audit/verify?fromSeq=2')->json('checked'))->toBe(count($this->host->store->all('audit')) - 1);

    $stored = (fn () => $this->tables['audit'])->call($this->host->store);
    array_find($stored, fn (AuditRecord $r) => $r->seq === 2)->data = '{"forged":true}';
    $broken = $this->api('POST', '/audit/verify')->assertOk()->json();
    expect($broken)->toMatchArray(['valid' => false, 'brokenAtSeq' => 2])
        ->and($broken['problem'])->toBeString();
});

it('purges entries past their retention and keeps a checkpoint the verifier accepts', function () {
    $this->api('PUT', '/settings', ['settings' => ['version' => 1, 'audit' => ['retentionDays' => ['definition' => 1]]], 'revision' => 0])->assertOk();
    expect($this->api('POST', '/audit/purge')->assertOk()->json())->toBe(['purged' => 0]);

    $this->host->clock->set($this->host->clock->now() + 2 * 86400000);
    expect($this->api('POST', '/audit/purge')->assertOk()->json())->toBe(['purged' => 2]);
    expect($this->api('POST', '/audit/verify')->assertOk()->json('valid'))->toBeTrue()
        ->and($this->api('GET', '/audit?category=audit&action=purged')->json('0.subjectId'))->toBe('definition')
        ->and(array_column($this->api('GET', '/audit?category=definition')->json(), 'seq'))->not->toContain(1, 2);
});
