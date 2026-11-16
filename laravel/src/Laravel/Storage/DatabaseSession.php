<?php

namespace Zouriel\Workflows\Laravel\Storage;

use Closure;
use Illuminate\Database\Query\Builder;
use LogicException;
use Throwable;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Storage\AuditHead;
use Zouriel\Workflows\Core\Storage\AuditPurgeResult;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\DefinitionRecord;
use Zouriel\Workflows\Core\Storage\DefinitionVersionRecord;
use Zouriel\Workflows\Core\Storage\EntityGateway;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Storage\SettingsRecord;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Storage\TriggerRecord;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;
use Zouriel\Workflows\Core\Storage\WorkflowSession;

/**
 * One unit of work of the database store: one transaction on the store's connection, begun at the first write.
 * Writes go to the database at once (inside the transaction), so the unit's own queries see them. Port of .NET
 * `EfWorkflowSession`.
 */
final class DatabaseSession implements WorkflowSession
{
    /** Tables whose rows carry a row version checked on every update (optimistic concurrency). */
    private const array VERSIONED = ['instances', 'tasks'];

    /** @var array<string, array{0: int, 1: bool}> "table\nid" => [the row version this unit last saw or wrote, whether this unit bumped it] */
    private array $versions = [];

    /** @var array<string, int> savepoint name => the transaction level it began at */
    private array $savepoints = [];

    /** The connection's transaction level before this unit began its own (null until the first write). */
    private ?int $baseLevel = null;

    private bool $done = false;

    private ?EntityGateway $gateway = null;

    /** @param  Closure(DatabaseSession): EntityGateway  $makeGateway */
    public function __construct(public readonly DatabaseStore $store, private readonly Closure $makeGateway) {}

    public function __destruct()
    {
        // The engine closes every session; this only guards sessions a caller dropped.
        $this->close();
    }

    public function entities(): EntityGateway
    {
        return $this->gateway ??= ($this->makeGateway)($this);
    }

    /**
     * Begins the unit's transaction before its first write (a savepoint inside one the app already opened). On SQLite it
     * takes the database's write lock at once, so the unit never upgrades a read lock under another writer.
     */
    public function write(): void
    {
        if ($this->done) {
            throw new LogicException('This unit of work has already ended.');
        }
        if ($this->baseLevel !== null) {
            return;
        }
        $db = $this->store->db;
        $this->baseLevel = $db->transactionLevel();
        DbErrors::map(function () use ($db) {
            $db->beginTransaction();
            if ($this->baseLevel === 0 && $this->store->driver() === 'sqlite') {
                $db->update('UPDATE '.$this->store->wrapTable('settings').' SET '.$db->getQueryGrammar()->wrap('key').' = '
                    .$db->getQueryGrammar()->wrap('key').' WHERE 1 = 0');
            }
        });
    }

    private function table(string $table): Builder
    {
        return $this->store->table($table);
    }

    /** Row locks are taken where the database has them; SQLite has one write lock, which `write()` already holds. */
    private function locking(Builder $query, bool $skipLocked): Builder
    {
        if (in_array($this->store->driver(), ['pgsql', 'mysql', 'mariadb'], true)) {
            $query->lock($skipLocked ? 'for update skip locked' : true);
        }

        return $query;
    }

    /**
     * @template T of object
     *
     * @param  class-string<T>  $class
     * @return T|null
     */
    private function one(string $table, string $class, Builder $query): ?object
    {
        $row = DbErrors::map(fn () => $query->first());
        if ($row === null) {
            return null;
        }
        $record = Rows::fromRow($class, $row);
        $this->noteVersion($table, $record);

        return $record;
    }

    /**
     * @template T of object
     *
     * @param  class-string<T>  $class
     * @return list<T>
     */
    private function many(string $table, string $class, Builder $query): array
    {
        $list = [];
        foreach (DbErrors::map(fn () => $query->get()) as $row) {
            $record = Rows::fromRow($class, $row);
            $this->noteVersion($table, $record);
            $list[] = $record;
        }

        return $list;
    }

    private function noteVersion(string $table, object $record): void
    {
        if (in_array($table, self::VERSIONED, true)) {
            $this->versions["{$table}\n{$record->id}"] ??= [$record->rowVersion, false];
        }
    }

    /** @param  array<string, mixed>  $row */
    private function insert(string $table, array $row, bool $ordered = true): void
    {
        $this->write();
        if ($ordered) {
            $row['ord'] = DatabaseStore::nextOrder();
        }
        DbErrors::map(fn () => $this->table($table)->insert($row));
    }

    /**
     * Saves a row by its key, inserting it when it is not there (the in-memory store's `put`).
     *
     * @param  array<string, mixed>  $key
     * @param  array<string, mixed>  $row
     */
    private function save(string $table, array $key, array $row, bool $ordered = true): void
    {
        $this->write();
        $values = array_diff_key($row, $key);
        $affected = DbErrors::map(fn () => $this->table($table)->where($key)->update($values));
        // MySQL counts only rows whose values changed, so a row that is there may report 0.
        if ($affected === 0 && ! DbErrors::map(fn () => $this->table($table)->where($key)->exists())) {
            $this->insert($table, $row, $ordered);
        }
    }

    /**
     * Updates an instance or task when its row version is still the one this unit read (spec: a unit that lost a race is
     * retried). The first update of the unit bumps the version; later ones keep it, as the in-memory store does at commit.
     */
    private function saveVersioned(string $table, InstanceRecord|TaskRecord $record): void
    {
        $this->write();
        $key = "{$table}\n{$record->id}";
        [$expected, $bumped] = $this->versions[$key] ?? [$record->rowVersion, false];
        $next = $bumped ? $expected : $expected + 1;
        $row = Rows::toRow($record);
        unset($row['id'], $row['ord']);
        $row['row_version'] = $next;
        $affected = DbErrors::map(fn () => $this->table($table)->where('id', $record->id)->where('row_version', $expected)->update($row));
        if ($affected === 0) {
            $current = DbErrors::map(fn () => $this->table($table)->where('id', $record->id)->value('row_version'));
            // MySQL reports 0 rows for an update that changed nothing; the row is still ours when its version is the one we wrote.
            if ($current === null || (int) $current !== $next || ! $bumped) {
                throw new WorkflowConcurrencyException('A '.substr($table, 0, -1).' changed under this unit; retry.');
            }
        }
        $this->versions[$key] = [$next, true];
        $record->rowVersion = $next;
    }

    // ── Definitions ───────────────────────────────────────────────────────

    public function definition(string $id): ?DefinitionRecord
    {
        return $this->one('definitions', DefinitionRecord::class, $this->table('definitions')->where('id', $id));
    }

    public function definitionByKey(string $key): ?DefinitionRecord
    {
        return $this->one('definitions', DefinitionRecord::class, $this->table('definitions')->where('key', $key));
    }

    public function definitions(): array
    {
        return $this->many('definitions', DefinitionRecord::class, $this->table('definitions')->orderBy('ord'));
    }

    public function addDefinition(DefinitionRecord $d): void
    {
        $this->insert('definitions', Rows::toRow($d));
    }

    public function updateDefinition(DefinitionRecord $d): void
    {
        $this->save('definitions', ['id' => $d->id], Rows::toRow($d));
    }

    public function deleteDefinition(string $id): void
    {
        $this->write();
        DbErrors::map(fn () => $this->table('definitions')->where('id', $id)->delete());
    }

    public function version(string $definitionId, int $version): ?DefinitionVersionRecord
    {
        return $this->one('versions', DefinitionVersionRecord::class, $this->table('versions')->where('definition_id', $definitionId)->where('version', $version));
    }

    public function versions(string $definitionId): array
    {
        return $this->many('versions', DefinitionVersionRecord::class, $this->table('versions')->where('definition_id', $definitionId)->orderBy('version'));
    }

    public function addVersion(DefinitionVersionRecord $v): void
    {
        $this->insert('versions', Rows::toRow($v), ordered: false);
    }

    public function triggers(): array
    {
        return $this->many('triggers', TriggerRecord::class, $this->table('triggers')->orderBy('ord'));
    }

    public function replaceTriggers(string $definitionId, array $triggers): void
    {
        $this->write();
        DbErrors::map(fn () => $this->table('triggers')->where('definition_id', $definitionId)->delete());
        foreach ($triggers as $t) {
            $this->insert('triggers', Rows::toRow($t));
        }
    }

    public function claimTriggerEvent(string $definitionId, string $eventId): bool
    {
        $this->write();
        if (DbErrors::map(fn () => $this->table('claims')->where('definition_id', $definitionId)->where('event_id', $eventId)->exists())) {
            return false;
        }
        // Written now: a concurrent claim of the same event fails on the primary key and its unit is retried.
        $this->insert('claims', ['definition_id' => $definitionId, 'event_id' => $eventId, 'claimed_at' => $this->store->clock->now()], ordered: false);

        return true;
    }

    // ── Instances, steps ──────────────────────────────────────────────────

    public function instance(string $id): ?InstanceRecord
    {
        return $this->one('instances', InstanceRecord::class, $this->table('instances')->where('id', $id));
    }

    private function filterInstances(InstanceQuery $q): Builder
    {
        $x = $this->table('instances');
        foreach (['definition_id' => $q->definitionId, 'definition_key' => $q->definitionKey, 'entity' => $q->entity, 'entity_id' => $q->entityId,
            'parent_id' => $q->parentId] as $column => $value) {
            if ($value !== null) {
                $x->where($column, $value);
            }
        }
        if ($q->statuses !== null) {
            $x->whereIn('status', $q->statuses);
        }
        if ($q->kinds !== null) {
            $x->whereIn('kind', $q->kinds);
        }

        return $x;
    }

    public function queryInstances(InstanceQuery $q): array
    {
        return $this->many('instances', InstanceRecord::class, $this->filterInstances($q)->orderByDesc('started_at')->orderByDesc('id')
            ->offset($q->skip)->limit(max(0, $q->take)));
    }

    public function countInstances(InstanceQuery $q): int
    {
        return DbErrors::map(fn () => $this->filterInstances($q)->count());
    }

    public function countInstancesByVersion(string $definitionId): array
    {
        $counts = [];
        $rows = DbErrors::map(fn () => $this->table('instances')->where('definition_id', $definitionId)->whereIn('kind', [InstanceKinds::AUTOMATION, InstanceKinds::RECORD])
            ->groupBy('version')->selectRaw('version, count(*) as n')->get());
        foreach ($rows as $row) {
            $counts[(int) $row->version] = (int) $row->n;
        }

        return $counts;
    }

    public function addInstance(InstanceRecord $i): void
    {
        $i->rowVersion = 1;
        $this->insert('instances', Rows::toRow($i));
        $this->versions["instances\n{$i->id}"] = [1, true];
    }

    public function updateInstance(InstanceRecord $i): void
    {
        $this->saveVersioned('instances', $i);
    }

    public function addStep(StepRecord $s): void
    {
        $this->insert('steps', Rows::toRow($s));
    }

    public function updateStep(StepRecord $s): void
    {
        $this->save('steps', ['id' => $s->id], Rows::toRow($s));
    }

    public function steps(string $instanceId): array
    {
        return $this->many('steps', StepRecord::class, $this->table('steps')->where('instance_id', $instanceId)->orderBy('started_at')->orderBy('ord'));
    }

    // ── Bookmarks ─────────────────────────────────────────────────────────

    public function addBookmark(BookmarkRecord $b): void
    {
        $this->save('bookmarks', ['id' => $b->id], Rows::toRow($b));
    }

    public function removeBookmark(string $id): void
    {
        $this->write();
        DbErrors::map(fn () => $this->table('bookmarks')->where('id', $id)->delete());
    }

    public function updateBookmark(BookmarkRecord $b): void
    {
        $this->save('bookmarks', ['id' => $b->id], Rows::toRow($b));
    }

    public function bookmark(string $id): ?BookmarkRecord
    {
        return $this->one('bookmarks', BookmarkRecord::class, $this->table('bookmarks')->where('id', $id));
    }

    public function bookmarks(string $instanceId): array
    {
        return $this->many('bookmarks', BookmarkRecord::class, $this->table('bookmarks')->where('instance_id', $instanceId)->orderBy('ord'));
    }

    public function claimDueBookmarks(int $now, int $lockUntil, int $limit): array
    {
        $this->write();
        $due = $this->many('bookmarks', BookmarkRecord::class, $this->locking($this->table('bookmarks')->whereNotNull('due_at')->where('due_at', '<=', $now)
            ->where(fn (Builder $w) => $w->whereNull('locked_until')->orWhere('locked_until', '<=', $now))
            ->orderBy('due_at')->orderBy('created_at')->orderBy('ord')->limit($limit), skipLocked: true));
        if ($due !== []) {
            DbErrors::map(fn () => $this->table('bookmarks')->whereIn('id', array_map(fn (BookmarkRecord $b) => $b->id, $due))->update(['locked_until' => $lockUntil]));
        }
        foreach ($due as $b) {
            $b->lockedUntil = $lockUntil;
        }

        return $due;
    }

    public function findBookmarks(string $kind, ?string $entity, ?string $entityId, ?string $signal): array
    {
        $x = $this->table('bookmarks')->where('kind', $kind);
        $entity === null ? $x->whereNull('entity') : $x->where('entity', $entity);
        $entityId === null ? $x->whereNull('entity_id') : $x->where('entity_id', $entityId);
        if ($signal !== null) {
            $x->where('signal', $signal);
        }

        return $this->many('bookmarks', BookmarkRecord::class, $x->orderBy('created_at')->orderBy('ord'));
    }

    public function definitionBookmarks(string $definitionId, string $kind): array
    {
        return $this->many('bookmarks', BookmarkRecord::class, $this->table('bookmarks')->where('definition_id', $definitionId)->where('kind', $kind)
            ->whereNull('instance_id')->orderBy('ord'));
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    public function task(string $id): ?TaskRecord
    {
        return $this->one('tasks', TaskRecord::class, $this->table('tasks')->where('id', $id));
    }

    private function filterTasks(TaskQuery $q): Builder
    {
        $x = $this->table('tasks');
        foreach (['instance_id' => $q->instanceId, 'root_instance_id' => $q->rootInstanceId, 'entity' => $q->entity, 'entity_id' => $q->entityId] as $column => $value) {
            if ($value !== null) {
                $x->where($column, $value);
            }
        }
        if ($q->statuses !== null) {
            $x->whereIn('status', $q->statuses);
        }
        if ($q->assigneeId !== null) {
            // Assignees are a JSON array of ids: find the id as the column writes it. LIKE may also match ids that differ
            // in case (MySQL and SQLite compare case-insensitively), so matches are checked exactly afterwards.
            $needle = str_replace(['!', '%', '_'], ['!!', '!%', '!_'], Values::serialize($q->assigneeId));
            $x->whereRaw($this->store->db->getQueryGrammar()->wrap('assignees')." LIKE ? ESCAPE '!'", ["%{$needle}%"]);
        }

        return $x;
    }

    private static function isAssignee(TaskRecord $t, string $assigneeId): bool
    {
        return in_array($assigneeId, array_map(Values::text(...), Values::fromJson($t->assignees) ?? []), true);
    }

    /** @return list<TaskRecord> */
    private function orderedTasks(TaskQuery $q, bool $paged): array
    {
        $x = $this->filterTasks($q)->orderByRaw('case when '.$this->store->db->getQueryGrammar()->wrap('status').' = ? then 0 else 1 end', [TaskStatus::OPEN])
            ->orderBy('due_at')->orderBy('ord');
        if ($paged) {
            $x->offset($q->skip)->limit(max(0, $q->take));
        }

        return $this->many('tasks', TaskRecord::class, $x);
    }

    public function queryTasks(TaskQuery $q): array
    {
        if ($q->assigneeId === null) {
            return $this->orderedTasks($q, true);
        }
        $list = array_values(array_filter($this->orderedTasks($q, false), fn (TaskRecord $t) => self::isAssignee($t, $q->assigneeId)));

        return array_slice($list, $q->skip, max(0, $q->take));
    }

    public function countTasks(TaskQuery $q): int
    {
        return $q->assigneeId === null
            ? DbErrors::map(fn () => $this->filterTasks($q)->count())
            : count(array_filter($this->orderedTasks($q, false), fn (TaskRecord $t) => self::isAssignee($t, $q->assigneeId)));
    }

    public function addTask(TaskRecord $t): void
    {
        $t->rowVersion = 1;
        $this->insert('tasks', Rows::toRow($t));
        $this->versions["tasks\n{$t->id}"] = [1, true];
    }

    public function updateTask(TaskRecord $t): void
    {
        $this->saveVersioned('tasks', $t);
    }

    // ── Outbox ────────────────────────────────────────────────────────────

    public function addOutbox(OutboxRecord $o): void
    {
        $this->insert('outbox', Rows::toRow($o), ordered: false);
    }

    public function updateOutbox(OutboxRecord $o): void
    {
        $this->save('outbox', ['id' => $o->id], Rows::toRow($o), ordered: false);
    }

    public function hasEarlierPendingOutbox(OutboxRecord $row): bool
    {
        if ($row->recordKey === null) {
            return false;
        }

        // Whatever its lock: a row another worker holds, or one backing off after a failure, still comes first.
        return DbErrors::map(fn () => $this->table('outbox')->where('record_key', $row->recordKey)->whereNull('processed_at')->where('id', '!=', $row->id)
            ->where(fn (Builder $w) => $w->where('created_at', '<', $row->createdAt)
                ->orWhere(fn (Builder $same) => $same->where('created_at', $row->createdAt)->where('id', '<', $row->id)))
            ->exists());
    }

    public function claimOutbox(int $now, int $lockUntil, int $limit): array
    {
        $this->write();
        $rows = $this->many('outbox', OutboxRecord::class, $this->locking($this->table('outbox')->whereNull('processed_at')
            ->where(fn (Builder $w) => $w->whereNull('locked_until')->orWhere('locked_until', '<=', $now))
            ->orderBy('created_at')->orderBy('id')->limit($limit), skipLocked: true));
        if ($rows !== []) {
            DbErrors::map(fn () => $this->table('outbox')->whereIn('id', array_map(fn (OutboxRecord $o) => $o->id, $rows))->update(['locked_until' => $lockUntil]));
        }
        foreach ($rows as $o) {
            $o->lockedUntil = $lockUntil;
        }

        return $rows;
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    public function auditHead(?string $tenant): ?AuditHead
    {
        // Locked for the rest of the unit, so concurrent writers queue here and the chain never forks. The head row is
        // the chain's own state; it never depends on the newest entry still existing (purges).
        $this->write();
        $key = $tenant ?? '';
        $row = DbErrors::map(fn () => $this->locking($this->table('audit_head')->where('tenant_key', $key), skipLocked: false)->first());
        if ($row === null) {
            // The first entry ever: a concurrent first writer fails on the primary key and is retried.
            DbErrors::map(fn () => $this->table('audit_head')->insert(['tenant_key' => $key, 'seq' => 0, 'hash' => '']));

            return null;
        }

        return (int) $row->seq === 0 ? null : new AuditHead((int) $row->seq, (string) $row->hash);
    }

    public function setAuditHead(?string $tenant, AuditHead $head): void
    {
        $this->save('audit_head', ['tenant_key' => $tenant ?? ''], ['tenant_key' => $tenant ?? '', 'seq' => $head->seq, 'hash' => $head->hash], ordered: false);
    }

    public function addAudit(AuditRecord $a): void
    {
        $this->insert('audit', Rows::toRow($a), ordered: false);
    }

    public function queryAudit(AuditQuery $q): array
    {
        $x = $this->table('audit')->where('tenant_key', $q->tenant ?? '');
        foreach (['category' => $q->category, 'action' => $q->action, 'entity' => $q->entity, 'entity_id' => $q->entityId, 'definition_id' => $q->definitionId,
            'actor_id' => $q->actorId] as $column => $value) {
            if ($value !== null) {
                $x->where($column, $value);
            }
        }
        if ($q->instanceId !== null) {
            $x->where(fn (Builder $w) => $w->where('instance_id', $q->instanceId)->orWhere('correlation_id', $q->instanceId));
        }
        if ($q->from !== null) {
            $x->where('at_ms', '>=', $q->from);
        }
        if ($q->to !== null) {
            $x->where('at_ms', '<', $q->to);
        }
        if ($q->before !== null) {
            $x->where('seq', '<', $q->before);
        }
        if ($q->fromSeq !== null) {
            $x->where('seq', '>=', $q->fromSeq);
        }
        $x = $q->fromSeq !== null ? $x->orderBy('seq') : $x->orderByDesc('seq');

        return $this->many('audit', AuditRecord::class, $x->limit(max(0, $q->take)));
    }

    public function purgeAudit(?string $tenant, string $category, int $before): AuditPurgeResult
    {
        $this->write();
        $victims = fn () => $this->table('audit')->where('tenant_key', $tenant ?? '')->where('category', $category)->where('at_ms', '<', $before);
        $seqs = DbErrors::map(fn () => $victims()->orderBy('seq')->pluck('seq')->map(fn ($seq) => (int) $seq)->all());
        $ranges = AuditPurgeResult::compress($seqs);
        $count = 0;
        foreach ($ranges as [$from, $to]) {
            $count += DbErrors::map(fn () => $victims()->whereBetween('seq', [$from, $to])->delete());
        }

        return new AuditPurgeResult($count, $ranges);
    }

    public function cleanup(int $processedOutboxBefore, int $claimsBefore): int
    {
        $this->write();

        return DbErrors::map(fn () => $this->table('outbox')->whereNotNull('processed_at')->where('processed_at', '<', $processedOutboxBefore)->delete()
            + $this->table('claims')->where('claimed_at', '<', $claimsBefore)->delete());
    }

    // ── Savepoints ────────────────────────────────────────────────────────

    public function createSavepoint(string $name): bool
    {
        $this->write();
        $this->savepoints[$name] = $this->store->db->transactionLevel();
        DbErrors::map(fn () => $this->store->db->beginTransaction());

        return true;
    }

    public function rollbackToSavepoint(string $name): void
    {
        if (! isset($this->savepoints[$name])) {
            return;
        }
        $this->store->db->rollBack($this->savepoints[$name]);
        unset($this->savepoints[$name]);
    }

    public function releaseSavepoint(string $name): void
    {
        if (! isset($this->savepoints[$name])) {
            return;
        }
        // Back to the level the savepoint began at (an action that left a transaction of its own open is closed with it).
        while ($this->store->db->transactionLevel() > $this->savepoints[$name]) {
            $this->store->db->commit();
        }
        unset($this->savepoints[$name]);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    public function settings(): ?SettingsRecord
    {
        return $this->one('settings', SettingsRecord::class, $this->table('settings')->where('key', 'global'));
    }

    public function saveSettings(SettingsRecord $s): void
    {
        $s->key = 'global';
        $this->save('settings', ['key' => 'global'], Rows::toRow($s), ordered: false);
    }

    // ── Unit of work ──────────────────────────────────────────────────────

    public function commit(): void
    {
        if ($this->done) {
            throw new LogicException('This session has already ended.');
        }
        if ($this->baseLevel !== null) {
            $db = $this->store->db;
            DbErrors::map(function () use ($db) {
                while ($db->transactionLevel() > $this->baseLevel) {
                    $db->commit();
                }
            });
        }
        $this->done = true;
    }

    public function close(): void
    {
        if ($this->done) {
            return;
        }
        $this->done = true;
        if ($this->baseLevel === null) {
            return;
        }
        try {
            $this->store->db->rollBack($this->baseLevel);
        } catch (Throwable) {
            // Rolling back after a failed statement can fail too; the caller already has the real error.
        }
    }
}
