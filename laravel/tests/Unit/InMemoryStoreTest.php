<?php

use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Audit\AuditWriter;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\InMemoryStore;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;
use Zouriel\Workflows\Core\Time\ManualClock;

// The in-memory store behaves like a database store (.NET CoreUnitTests / TenancyTests): units see their own writes
// only, conflicting writes are refused at commit, savepoints undo, ids page, and audit chains are kept per tenant.

function memoryStore(): InMemoryStore
{
    return new InMemoryStore(WorkflowRegistry::fromJson('{"specVersion":1,"entities":[{"key":"thing","label":"Thing","fields":[{"key":"name","label":"Name","type":"text"}]}]}'),
        new ManualClock(1_800_000_000_000));
}

function storedInstance(InMemoryStore $store, string $id, ?string $liveKey = null): void
{
    $i = new InstanceRecord;
    $i->id = $id;
    $i->liveKey = $liveKey;
    $s = $store->begin();
    $s->addInstance($i);
    $s->commit();
}

it('refuses a write to an instance that changed since the unit read it', function () {
    $store = memoryStore();
    storedInstance($store, 'i1');
    $first = $store->begin();
    $second = $store->begin();
    $mine = $first->instance('i1');
    $theirs = $second->instance('i1');
    $theirs->status = 'waiting';
    $second->updateInstance($theirs);
    $second->commit();
    $mine->status = 'incident';
    $first->updateInstance($mine);

    expect(fn () => $first->commit())->toThrow(WorkflowConcurrencyException::class)
        ->and($store->begin()->instance('i1')->status)->toBe('waiting')
        ->and($store->begin()->instance('i1')->rowVersion)->toBe(2);
});

it('keeps a unit\'s writes to itself until it commits', function () {
    $store = memoryStore();
    $s = $store->begin();
    $s->records->createWithId('thing', 't1', (object) ['name' => 'Kettle']);

    expect($store->begin()->entities()->get('thing', 't1'))->toBeNull()
        ->and($s->entities()->get('thing', 't1')->values->name)->toBe('Kettle');
    $s->commit();
    expect($store->peekRecord('thing', 't1')->values->name)->toBe('Kettle');
});

it('refuses two live instances with the same live key', function () {
    $store = memoryStore();
    storedInstance($store, 'i1', 'record:d:thing:t1');

    expect(fn () => storedInstance($store, 'i2', 'record:d:thing:t1'))->toThrow(WorkflowConcurrencyException::class);
    storedInstance($store, 'i3', 'record:d:thing:t2');
    expect($store->all('instances'))->toHaveCount(2);
});

it('refuses claiming one trigger event twice, in one unit or two', function () {
    $store = memoryStore();
    $first = $store->begin();
    $second = $store->begin();

    expect($first->claimTriggerEvent('d', 'e1'))->toBeTrue()
        ->and($first->claimTriggerEvent('d', 'e1'))->toBeFalse()
        ->and($second->claimTriggerEvent('d', 'e1'))->toBeTrue();
    $first->commit();
    expect(fn () => $second->commit())->toThrow(WorkflowConcurrencyException::class);
});

it('undoes the writes made after a savepoint', function () {
    $store = memoryStore();
    $s = $store->begin();
    $s->records->createWithId('thing', 'kept', (object) ['name' => 'Kept']);
    $s->createSavepoint('sp');
    $s->records->createWithId('thing', 'undone', (object) ['name' => 'Undone']);
    $s->rollbackToSavepoint('sp');
    $s->commit();

    expect($store->peekRecord('thing', 'kept'))->not->toBeNull()
        ->and($store->peekRecord('thing', 'undone'))->toBeNull();
});

it('lists ids page by page in key order', function () {
    $store = memoryStore();
    foreach (['c', 'a', 'b', 'd'] as $id) {
        $store->createRecord('thing', $id, (object) ['name' => $id]);
    }
    $gateway = $store->begin()->entities();

    expect($gateway->listIds('thing', null, 2))->toBe(['a', 'b'])
        ->and($gateway->listIds('thing', 'b', 2))->toBe(['c', 'd'])
        ->and($gateway->listIds('thing', 'd', 2))->toBe([]);
});

it('keeps one audit chain per tenant and refuses a commit whose chain head moved', function () {
    $store = memoryStore();
    $write = function (?string $tenant, string $subject) use ($store) {
        $s = $store->begin();
        $writer = new AuditWriter($s, $tenant, fn () => 1_800_000_000_000);
        $writer->write(new AuditEntry('test', 'done', 'thing', $subject));
        $writer->flush();
        $s->commit();
    };
    $write(null, 'a');
    $write('north', 'b');
    $write(null, 'c');

    expect(array_map(fn ($a) => [$a->seq, $a->subjectId], $store->begin()->queryAudit(new AuditQuery(fromSeq: 1))))->toBe([[1, 'a'], [2, 'c']])
        ->and(array_map(fn ($a) => [$a->seq, $a->subjectId], $store->begin()->queryAudit(new AuditQuery('north', fromSeq: 1))))->toBe([[1, 'b']]);

    $slow = $store->begin();
    $slowWriter = new AuditWriter($slow, null, fn () => 1_800_000_000_000);
    $slowWriter->write(new AuditEntry('test', 'done', 'thing', 'slow'));
    $slowWriter->flush();
    $write(null, 'fast');
    expect(fn () => $slow->commit())->toThrow(WorkflowConcurrencyException::class);
});
