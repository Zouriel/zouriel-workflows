<?php

use Illuminate\Support\Facades\DB;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\AuditHead;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;
use Zouriel\Workflows\Core\Time\ManualClock;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;
use Zouriel\Workflows\Tests\Support\DatabaseHostStore;
use Zouriel\Workflows\Tests\Support\DatabaseTestCase;
use Zouriel\Workflows\Tests\Support\TestDatabase;

uses(DatabaseTestCase::class);

// The database store's own guarantees (ported from .NET StoreBehaviourTests / StoreEdgeCaseTests): races end in
// WorkflowConcurrencyException so the engine retries, claims are leased, and a unit commits or rolls back as one.

function store(string $connection = 'testing', ?ManualClock $clock = null): DatabaseStore
{
    return (new DatabaseHostStore(DB::connection($connection), new WorkflowRegistry, $clock ?? new ManualClock(1000)))->inner;
}

function instanceRow(string $id, ?string $liveKey = null): InstanceRecord
{
    $i = new InstanceRecord;
    $i->id = $id;
    $i->definitionId = 'd';
    $i->definitionKey = 'k';
    $i->correlationId = $id;
    $i->liveKey = $liveKey;

    return $i;
}

function dueTimer(string $id, int $dueAt): BookmarkRecord
{
    $b = new BookmarkRecord;
    $b->id = $id;
    $b->dueAt = $dueAt;
    $b->createdAt = $dueAt;

    return $b;
}

function commitWith(DatabaseStore $store, callable $work): mixed
{
    $s = $store->begin();
    try {
        $result = $work($s);
        $s->commit();

        return $result;
    } finally {
        $s->close();
    }
}

it('refuses an update to an instance another unit changed since it was read', function () {
    $store = store();
    commitWith($store, fn ($s) => $s->addInstance(instanceRow('i1')));
    $a = $store->begin();
    $b = $store->begin();
    $mine = $a->instance('i1');
    $theirs = $b->instance('i1');
    $theirs->status = 'completed';
    $b->updateInstance($theirs);
    $b->commit();

    $mine->status = 'failed';
    expect(fn () => $a->updateInstance($mine))->toThrow(WorkflowConcurrencyException::class);
    $a->close();
    expect(DB::table('wf_instances')->value('status'))->toBe('completed')
        ->and((int) DB::table('wf_instances')->value('row_version'))->toBe(2);
});

it('lets one unit update its own instance many times', function () {
    $store = store();
    commitWith($store, fn ($s) => $s->addInstance(instanceRow('i1')));
    commitWith($store, function ($s) {
        $i = $s->instance('i1');
        foreach (['waiting', 'running', 'completed'] as $status) {
            $i->status = $status;
            $s->updateInstance($i);
        }
    });

    expect(DB::table('wf_instances')->value('status'))->toBe('completed')
        ->and((int) DB::table('wf_instances')->value('row_version'))->toBe(2);
});

it('refuses a second live instance with the same live key as a race', function () {
    $store = store();
    commitWith($store, fn ($s) => $s->addInstance(instanceRow('i1', 'record:d:application:1')));

    expect(fn () => commitWith($store, fn ($s) => $s->addInstance(instanceRow('i2', 'record:d:application:1'))))->toThrow(WorkflowConcurrencyException::class);
    commitWith($store, fn ($s) => $s->addInstance(instanceRow('i3', 'record:d:application:2')));
    expect(DB::table('wf_instances')->pluck('id')->sort()->values()->all())->toBe(['i1', 'i3']);
});

it('claims a trigger event once', function () {
    $store = store();
    expect(commitWith($store, fn ($s) => $s->claimTriggerEvent('d', 'e1')))->toBeTrue()
        ->and(commitWith($store, fn ($s) => $s->claimTriggerEvent('d', 'e1')))->toBeFalse()
        ->and(commitWith($store, fn ($s) => $s->claimTriggerEvent('other', 'e1')))->toBeTrue();
});

it('leases claimed timers: another worker skips them until the lease runs out', function () {
    $store = store();
    commitWith($store, function ($s) {
        $s->addBookmark(dueTimer('t1', 500));
        $s->addBookmark(dueTimer('t2', 600));
        $s->addBookmark(dueTimer('later', 5000));
    });

    $first = commitWith($store, fn ($s) => $s->claimDueBookmarks(1000, 61000, 10));
    $second = commitWith($store, fn ($s) => $s->claimDueBookmarks(1000, 61000, 10));
    $afterLease = commitWith($store, fn ($s) => $s->claimDueBookmarks(61000, 121000, 10));

    expect(array_map(fn ($b) => $b->id, $first))->toBe(['t1', 't2'])
        ->and($second)->toBe([])
        ->and(array_map(fn ($b) => $b->id, $afterLease))->toBe(['t1', 't2', 'later']);
});

it('leases claimed outbox rows the same way', function () {
    $store = store();
    commitWith($store, function ($s) {
        foreach (['a', 'b'] as $n => $id) {
            $o = new OutboxRecord;
            $o->id = $id;
            $o->createdAt = 100 + $n;
            $s->addOutbox($o);
        }
    });

    expect(array_map(fn ($o) => $o->id, commitWith($store, fn ($s) => $s->claimOutbox(1000, 2000, 10))))->toBe(['a', 'b'])
        ->and(commitWith($store, fn ($s) => $s->claimOutbox(1000, 2000, 10)))->toBe([])
        ->and(array_map(fn ($o) => $o->id, commitWith($store, fn ($s) => $s->claimOutbox(2000, 3000, 1))))->toBe(['a']);
});

it('never gives two workers the same timer while the first is still claiming it', function () {
    $mine = store();
    $theirs = store('other');
    commitWith($mine, function ($s) {
        $s->addBookmark(dueTimer('t1', 500));
        $s->addBookmark(dueTimer('t2', 600));
    });

    // The first worker has claimed t1 and not committed yet: the second skips the locked row instead of waiting for it.
    $a = $mine->begin();
    expect(array_map(fn ($b) => $b->id, $a->claimDueBookmarks(1000, 61000, 1)))->toBe(['t1']);
    $b = $theirs->begin();
    expect(array_map(fn ($b) => $b->id, $b->claimDueBookmarks(1000, 61000, 10)))->toBe(['t2']);
    $b->commit();
    $a->commit();

    expect(commitWith($mine, fn ($s) => $s->claimDueBookmarks(1000, 61000, 10)))->toBe([]);
})->skip(TestDatabase::driver() !== 'pgsql', 'needs two connections to one database (DB_CONNECTION=pgsql)');

it('rolls back everything a unit wrote when it is closed without committing', function () {
    $store = store();
    $s = $store->begin();
    $s->addInstance(instanceRow('i1'));
    $s->addBookmark(dueTimer('t1', 500));
    expect(DB::transactionLevel())->toBe(1);
    $s->close();

    expect(DB::transactionLevel())->toBe(0)
        ->and(DB::table('wf_instances')->count())->toBe(0)
        ->and(DB::table('wf_bookmarks')->count())->toBe(0);
});

it('starts no transaction for a unit that only reads', function () {
    $store = store();
    $s = $store->begin();
    $s->definitions();
    $s->instance('nope');
    expect(DB::transactionLevel())->toBe(0);
    $s->close();
});

it('undoes the work after a savepoint and keeps what came before it', function () {
    $store = store();
    commitWith($store, function ($s) {
        $s->addInstance(instanceRow('kept'));
        expect($s->createSavepoint('action'))->toBeTrue();
        $s->addInstance(instanceRow('undone'));
        DB::table('wf_test_records')->insert(['entity' => 'x', 'id' => '1', 'ord' => 1, 'json' => '{}']);
        $s->rollbackToSavepoint('action');
        $s->createSavepoint('next');
        $s->addInstance(instanceRow('released'));
        $s->releaseSavepoint('next');
    });

    expect(DB::table('wf_instances')->orderBy('id')->pluck('id')->all())->toBe(['kept', 'released'])
        ->and(DB::table('wf_test_records')->count())->toBe(0)
        ->and(DB::transactionLevel())->toBe(0);
});

it('commits a unit into a transaction the app already opened, and rolls back with it', function () {
    $store = store();
    DB::beginTransaction();
    commitWith($store, fn ($s) => $s->addInstance(instanceRow('i1')));
    expect(DB::transactionLevel())->toBe(1)
        ->and(DB::table('wf_instances')->count())->toBe(1);
    DB::rollBack();

    expect(DB::table('wf_instances')->count())->toBe(0);
});

it('keeps each tenant\'s audit chain apart', function () {
    $store = store();
    $entry = fn (?string $tenant, int $seq) => new AuditRecord(id: "{$tenant}-{$seq}", at: '2026-10-19T09:00:00.000Z', actorType: 'system', category: 'definition',
        action: 'created', subjectType: 'definition', subjectId: 'd', correlationId: 'c', tenant: $tenant, seq: $seq, hash: "h{$seq}");
    commitWith($store, function ($s) use ($entry) {
        $s->auditHead('north');
        $s->addAudit($entry('north', 1));
        $s->setAuditHead('north', new AuditHead(1, 'h1'));
        $s->auditHead(null);
        $s->addAudit($entry(null, 1));
        $s->addAudit($entry(null, 2));
        $s->setAuditHead(null, new AuditHead(2, 'h2'));
    });

    $north = commitWith($store, fn ($s) => $s->queryAudit(new AuditQuery(tenant: 'north')));
    $default = commitWith($store, fn ($s) => $s->queryAudit(new AuditQuery));
    expect(array_map(fn ($a) => [$a->id, $a->tenant], $north))->toBe([['north-1', 'north']])
        ->and(array_map(fn ($a) => $a->id, $default))->toBe(['-2', '-1'])
        ->and(commitWith($store, fn ($s) => $s->auditHead('north'))->seq)->toBe(1)
        ->and(commitWith($store, fn ($s) => $s->purgeAudit(null, 'definition', PHP_INT_MAX))->ranges)->toBe([[1, 2]])
        ->and(commitWith($store, fn ($s) => $s->queryAudit(new AuditQuery(tenant: 'north'))))->toHaveCount(1);
});

it('finds tasks by assignee exactly, whatever the JSON escaping', function () {
    $store = store();
    $task = function (string $id, array $assignees) {
        $t = new TaskRecord;
        $t->id = $id;
        $t->assignees = json_encode($assignees, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);

        return $t;
    };
    commitWith($store, function ($s) use ($task) {
        $s->addTask($task('t1', ['ali+1@example.com', 'Rana']));
        $s->addTask($task('t2', ['rana']));
        $s->addTask($task('t3', ['a_i']));
    });
    $ids = fn (string $assignee) => array_map(fn ($t) => $t->id, commitWith($store, fn ($s) => $s->queryTasks(new TaskQuery(assigneeId: $assignee))));

    expect($ids('rana'))->toBe(['t2'])
        ->and($ids('Rana'))->toBe(['t1'])
        ->and($ids('ali+1@example.com'))->toBe(['t1'])
        ->and($ids('a%i'))->toBe([])
        ->and($ids('a_i'))->toBe(['t3'])
        ->and(commitWith($store, fn ($s) => $s->countTasks(new TaskQuery(assigneeId: 'rana'))))->toBe(1);
});
