<?php

namespace Zouriel\Workflows\Core\Storage;

use LogicException;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Time\Instant;

/** One unit of work of the in-memory store: writes go to an overlay until commit. Port of .NET `InMemoryWorkflowStore.Session`. */
final class InMemorySession implements WorkflowSession
{
    /** Tables whose rows carry a row version checked at commit (optimistic concurrency, like a database store). */
    private const array VERSIONED = ['instances', 'tasks'];

    /** @var array<string, array<string, object|null>> writes by table and key (null = deleted), in the order made */
    private array $overlay = [];

    /** @var array<string, int> row versions as first read in this unit ("table\nkey") */
    private array $readVersions = [];

    /** @var array<string, AuditHead|null> */
    private array $readHeads = [];

    /** @var array<string, AuditHead> */
    private array $newHeads = [];

    /** @var array<string, array<string, array<string, object|null>>> */
    private array $savepoints = [];

    private bool $done = false;

    public readonly InMemoryGateway $records;

    public function __construct(public readonly InMemoryStore $store)
    {
        $this->records = new InMemoryGateway($this);
    }

    public function entities(): EntityGateway
    {
        return $this->records;
    }

    // ── Generic table access ──────────────────────────────────────────────

    /**
     * @template T of object
     *
     * @param  class-string<T>  $class
     * @return T|null
     */
    public function get(string $table, string $key, string $class = 'object'): ?object
    {
        if (array_key_exists($key, $this->overlay[$table] ?? [])) {
            $row = $this->overlay[$table][$key];

            return $row === null ? null : clone $row;
        }
        $row = $this->store->tables[$table][$key] ?? null;
        if ($row === null) {
            return null;
        }
        $this->noteVersion($table, $key, $row);

        return clone $row;
    }

    /** @return list<object> copies of every row this unit sees, in insertion order */
    public function all(string $table): array
    {
        $result = [];
        foreach ($this->store->tables[$table] ?? [] as $key => $row) {
            $result[(string) $key] = $row;
            $this->noteVersion($table, (string) $key, $row);
        }
        foreach ($this->overlay[$table] ?? [] as $key => $row) {
            if ($row === null) {
                unset($result[(string) $key]);
            } else {
                $result[(string) $key] = $row;
            }
        }

        return array_values(array_map(fn (object $row) => clone $row, $result));
    }

    public function put(string $table, string $key, object $row): void
    {
        $this->overlay[$table][$key] = clone $row;
    }

    public function remove(string $table, string $key): void
    {
        $this->overlay[$table][$key] = null;
    }

    private function noteVersion(string $table, string $key, object $row): void
    {
        if (! in_array($table, self::VERSIONED, true) || array_key_exists($key, $this->overlay[$table] ?? [])) {
            return;
        }
        $this->readVersions["{$table}\n{$key}"] ??= $row->rowVersion;
    }

    // ── Definitions ───────────────────────────────────────────────────────

    public function definition(string $id): ?DefinitionRecord
    {
        return $this->get('definitions', $id);
    }

    public function definitionByKey(string $key): ?DefinitionRecord
    {
        return array_find($this->definitions(), fn (DefinitionRecord $d) => $d->key === $key);
    }

    public function definitions(): array
    {
        return $this->all('definitions');
    }

    public function addDefinition(DefinitionRecord $d): void
    {
        $this->put('definitions', $d->id, $d);
    }

    public function updateDefinition(DefinitionRecord $d): void
    {
        $this->put('definitions', $d->id, $d);
    }

    public function deleteDefinition(string $id): void
    {
        $this->remove('definitions', $id);
    }

    public function version(string $definitionId, int $version): ?DefinitionVersionRecord
    {
        return $this->get('versions', "{$definitionId}:{$version}");
    }

    public function versions(string $definitionId): array
    {
        return array_values(array_filter($this->all('versions'), fn (DefinitionVersionRecord $v) => $v->definitionId === $definitionId));
    }

    public function addVersion(DefinitionVersionRecord $v): void
    {
        $this->put('versions', "{$v->definitionId}:{$v->version}", $v);
    }

    public function triggers(): array
    {
        return $this->all('triggers');
    }

    public function replaceTriggers(string $definitionId, array $triggers): void
    {
        foreach ($this->all('triggers') as $t) {
            if ($t->definitionId === $definitionId) {
                $this->remove('triggers', $t->id);
            }
        }
        foreach ($triggers as $t) {
            $this->put('triggers', $t->id, $t);
        }
    }

    public function claimTriggerEvent(string $definitionId, string $eventId): bool
    {
        $key = "{$definitionId}:{$eventId}";
        if ($this->get('claims', $key) !== null) {
            return false;
        }
        $this->put('claims', $key, new TriggerClaim($key, $this->store->clock->now()));

        return true;
    }

    // ── Instances, steps ──────────────────────────────────────────────────

    public function instance(string $id): ?InstanceRecord
    {
        return $this->get('instances', $id);
    }

    /** @return list<InstanceRecord> */
    private function filterInstances(InstanceQuery $q): array
    {
        return array_values(array_filter($this->all('instances'), fn (InstanceRecord $i) => ($q->definitionId === null || $i->definitionId === $q->definitionId)
            && ($q->definitionKey === null || $i->definitionKey === $q->definitionKey) && ($q->entity === null || $i->entity === $q->entity)
            && ($q->entityId === null || $i->entityId === $q->entityId) && ($q->statuses === null || in_array($i->status, $q->statuses, true))
            && ($q->kinds === null || in_array($i->kind, $q->kinds, true)) && ($q->parentId === null || $i->parentId === $q->parentId)));
    }

    public function queryInstances(InstanceQuery $q): array
    {
        $list = $this->filterInstances($q);
        usort($list, fn (InstanceRecord $a, InstanceRecord $b) => [$b->startedAt, $b->id] <=> [$a->startedAt, $a->id]);

        return array_slice($list, $q->skip, max(0, $q->take));
    }

    public function countInstances(InstanceQuery $q): int
    {
        return count($this->filterInstances($q));
    }

    public function countInstancesByVersion(string $definitionId): array
    {
        $counts = [];
        foreach ($this->all('instances') as $i) {
            if ($i->definitionId === $definitionId && in_array($i->kind, [InstanceKinds::AUTOMATION, InstanceKinds::RECORD], true)) {
                $counts[$i->version] = ($counts[$i->version] ?? 0) + 1;
            }
        }

        return $counts;
    }

    public function addInstance(InstanceRecord $i): void
    {
        $this->put('instances', $i->id, $i);
    }

    public function updateInstance(InstanceRecord $i): void
    {
        $this->put('instances', $i->id, $i);
    }

    public function addStep(StepRecord $s): void
    {
        $this->put('steps', $s->id, $s);
    }

    public function updateStep(StepRecord $s): void
    {
        $this->put('steps', $s->id, $s);
    }

    public function steps(string $instanceId): array
    {
        $list = array_values(array_filter($this->all('steps'), fn (StepRecord $s) => $s->instanceId === $instanceId));
        // A stable sort by start time keeps steps that started in the same millisecond in the order they were added.
        usort($list, fn (StepRecord $a, StepRecord $b) => $a->startedAt <=> $b->startedAt);

        return $list;
    }

    // ── Bookmarks ─────────────────────────────────────────────────────────

    public function addBookmark(BookmarkRecord $b): void
    {
        $this->put('bookmarks', $b->id, $b);
    }

    public function removeBookmark(string $id): void
    {
        $this->remove('bookmarks', $id);
    }

    public function updateBookmark(BookmarkRecord $b): void
    {
        $this->put('bookmarks', $b->id, $b);
    }

    public function bookmark(string $id): ?BookmarkRecord
    {
        return $this->get('bookmarks', $id);
    }

    public function bookmarks(string $instanceId): array
    {
        return array_values(array_filter($this->all('bookmarks'), fn (BookmarkRecord $b) => $b->instanceId === $instanceId));
    }

    public function claimDueBookmarks(int $now, int $lockUntil, int $limit): array
    {
        $due = array_values(array_filter($this->all('bookmarks'), fn (BookmarkRecord $b) => $b->dueAt !== null && $b->dueAt <= $now
            && ($b->lockedUntil === null || $b->lockedUntil <= $now)));
        usort($due, fn (BookmarkRecord $a, BookmarkRecord $b) => [$a->dueAt, $a->createdAt] <=> [$b->dueAt, $b->createdAt]);
        $due = array_slice($due, 0, $limit);
        foreach ($due as $b) {
            $b->lockedUntil = $lockUntil;
            $this->put('bookmarks', $b->id, $b);
        }

        return $due;
    }

    public function findBookmarks(string $kind, ?string $entity, ?string $entityId, ?string $signal): array
    {
        $list = array_values(array_filter($this->all('bookmarks'), fn (BookmarkRecord $b) => $b->kind === $kind && $b->entity === $entity
            && $b->entityId === $entityId && ($signal === null || $b->signal === $signal)));
        usort($list, fn (BookmarkRecord $a, BookmarkRecord $b) => $a->createdAt <=> $b->createdAt);

        return $list;
    }

    public function definitionBookmarks(string $definitionId, string $kind): array
    {
        return array_values(array_filter($this->all('bookmarks'), fn (BookmarkRecord $b) => $b->definitionId === $definitionId && $b->kind === $kind
            && $b->instanceId === null));
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    public function task(string $id): ?TaskRecord
    {
        return $this->get('tasks', $id);
    }

    /** @return list<TaskRecord> */
    private function filterTasks(TaskQuery $q): array
    {
        return array_values(array_filter($this->all('tasks'), function (TaskRecord $t) use ($q) {
            if (($q->instanceId !== null && $t->instanceId !== $q->instanceId) || ($q->rootInstanceId !== null && $t->rootInstanceId !== $q->rootInstanceId)
                || ($q->entity !== null && $t->entity !== $q->entity) || ($q->entityId !== null && $t->entityId !== $q->entityId)
                || ($q->statuses !== null && ! in_array($t->status, $q->statuses, true))) {
                return false;
            }

            return $q->assigneeId === null || in_array($q->assigneeId, array_map(Values::text(...), Values::fromJson($t->assignees) ?? []), true);
        }));
    }

    public function queryTasks(TaskQuery $q): array
    {
        $list = $this->filterTasks($q);
        usort($list, fn (TaskRecord $a, TaskRecord $b) => [$a->status === TaskStatus::OPEN ? 0 : 1, $a->dueAt] <=> [$b->status === TaskStatus::OPEN ? 0 : 1, $b->dueAt]);

        return array_slice($list, $q->skip, max(0, $q->take));
    }

    public function countTasks(TaskQuery $q): int
    {
        return count($this->filterTasks($q));
    }

    public function addTask(TaskRecord $t): void
    {
        $this->put('tasks', $t->id, $t);
    }

    public function updateTask(TaskRecord $t): void
    {
        $this->put('tasks', $t->id, $t);
    }

    // ── Outbox ────────────────────────────────────────────────────────────

    public function addOutbox(OutboxRecord $o): void
    {
        $this->put('outbox', $o->id, $o);
    }

    public function updateOutbox(OutboxRecord $o): void
    {
        $this->put('outbox', $o->id, $o);
    }

    public function hasEarlierPendingOutbox(OutboxRecord $row): bool
    {
        return $row->recordKey !== null && array_any($this->all('outbox'), fn (OutboxRecord $o) => $o->id !== $row->id && $o->recordKey === $row->recordKey
            && $o->processedAt === null && ($o->createdAt < $row->createdAt || ($o->createdAt === $row->createdAt && strcmp($o->id, $row->id) < 0)));
    }

    public function claimOutbox(int $now, int $lockUntil, int $limit): array
    {
        $rows = array_values(array_filter($this->all('outbox'), fn (OutboxRecord $o) => $o->processedAt === null && ($o->lockedUntil === null || $o->lockedUntil <= $now)));
        usort($rows, fn (OutboxRecord $a, OutboxRecord $b) => $a->createdAt <=> $b->createdAt ?: strcmp($a->id, $b->id));
        $rows = array_slice($rows, 0, $limit);
        foreach ($rows as $o) {
            $o->lockedUntil = $lockUntil;
            $this->put('outbox', $o->id, $o);
        }

        return $rows;
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    public function auditHead(?string $tenant): ?AuditHead
    {
        $key = $tenant ?? '';
        if (isset($this->newHeads[$key])) {
            return $this->newHeads[$key];
        }
        if (array_key_exists($key, $this->readHeads)) {
            return $this->readHeads[$key];
        }

        return $this->readHeads[$key] = $this->store->auditHeads[$key] ?? null;
    }

    public function setAuditHead(?string $tenant, AuditHead $head): void
    {
        $this->newHeads[$tenant ?? ''] = $head;
    }

    public function addAudit(AuditRecord $a): void
    {
        $this->put('audit', self::auditKey($a), $a);
    }

    /** Each tenant has its own chain (and sequence numbers), so the tenant is part of the key. */
    private static function auditKey(AuditRecord $a): string
    {
        return $a->tenant.'|'.sprintf('%019d', $a->seq);
    }

    public function queryAudit(AuditQuery $q): array
    {
        $tenant = $q->tenant ?? '';
        $rows = array_values(array_filter($this->all('audit'), function (AuditRecord $a) use ($q, $tenant) {
            $at = Instant::parse($a->at) ?? 0;

            return ($a->tenant ?? '') === $tenant && ($q->category === null || $a->category === $q->category) && ($q->action === null || $a->action === $q->action)
                && ($q->entity === null || $a->entity === $q->entity) && ($q->entityId === null || $a->entityId === $q->entityId)
                && ($q->instanceId === null || $a->instanceId === $q->instanceId || $a->correlationId === $q->instanceId)
                && ($q->definitionId === null || $a->definitionId === $q->definitionId) && ($q->actorId === null || $a->actorId === $q->actorId)
                && ($q->from === null || $at >= $q->from) && ($q->to === null || $at < $q->to) && ($q->before === null || $a->seq < $q->before)
                && ($q->fromSeq === null || $a->seq >= $q->fromSeq);
        }));
        usort($rows, fn (AuditRecord $a, AuditRecord $b) => $q->fromSeq !== null ? $a->seq <=> $b->seq : $b->seq <=> $a->seq);

        return array_slice($rows, 0, max(0, $q->take));
    }

    public function purgeAudit(?string $tenant, string $category, int $before): AuditPurgeResult
    {
        $victims = array_values(array_filter($this->all('audit'), fn (AuditRecord $a) => ($a->tenant ?? '') === ($tenant ?? '') && $a->category === $category
            && (Instant::parse($a->at) ?? 0) < $before));
        usort($victims, fn (AuditRecord $a, AuditRecord $b) => $a->seq <=> $b->seq);
        foreach ($victims as $v) {
            $this->remove('audit', self::auditKey($v));
        }

        return new AuditPurgeResult(count($victims), AuditPurgeResult::compress(array_map(fn (AuditRecord $a) => $a->seq, $victims)));
    }

    public function cleanup(int $processedOutboxBefore, int $claimsBefore): int
    {
        $n = 0;
        foreach ($this->all('outbox') as $o) {
            if ($o->processedAt !== null && $o->processedAt < $processedOutboxBefore) {
                $this->remove('outbox', $o->id);
                $n++;
            }
        }
        foreach ($this->all('claims') as $c) {
            if ($c->claimedAt < $claimsBefore) {
                $this->remove('claims', $c->key);
                $n++;
            }
        }

        return $n;
    }

    public function createSavepoint(string $name): bool
    {
        $this->savepoints[$name] = $this->overlay;

        return true;
    }

    public function rollbackToSavepoint(string $name): void
    {
        if (isset($this->savepoints[$name])) {
            $this->overlay = $this->savepoints[$name];
            unset($this->savepoints[$name]);
        }
    }

    public function releaseSavepoint(string $name): void
    {
        unset($this->savepoints[$name]);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    public function settings(): ?SettingsRecord
    {
        return $this->get('settings', 'global');
    }

    public function saveSettings(SettingsRecord $s): void
    {
        $this->put('settings', 'global', $s);
    }

    // ── Commit ────────────────────────────────────────────────────────────

    public function commit(): void
    {
        if ($this->done) {
            throw new LogicException('This session has already been committed.');
        }
        $tables = &$this->store->tables;
        // Audit entries form one chain: refuse a commit whose chain head moved under us.
        foreach (array_keys($this->newHeads) as $tenant) {
            if (($this->store->auditHeads[$tenant] ?? null) != ($this->readHeads[$tenant] ?? null)) {
                throw new WorkflowConcurrencyException('The audit chain moved; retry.');
            }
        }
        // Live keys are unique (a lifecycle's live record instance, a running serial automation).
        $liveKeys = [];
        foreach ($tables['instances'] ?? [] as $key => $row) {
            if (! array_key_exists($key, $this->overlay['instances'] ?? []) && $row->liveKey !== null) {
                $liveKeys[$row->liveKey] = (string) $key;
            }
        }
        foreach ($this->overlay['instances'] ?? [] as $key => $row) {
            if ($row !== null && $row->liveKey !== null) {
                if (isset($liveKeys[$row->liveKey]) && $liveKeys[$row->liveKey] !== (string) $key) {
                    throw new WorkflowConcurrencyException("Another unit started the same instance ({$row->liveKey}); retry.");
                }
                $liveKeys[$row->liveKey] = (string) $key;
            }
        }
        // Optimistic concurrency: a row written here must not have changed since this unit read it.
        foreach ($this->overlay as $table => $rows) {
            foreach ($rows as $key => $row) {
                $stored = $tables[$table][$key] ?? null;
                if ($table === 'claims' && $row !== null && $stored !== null) {
                    throw new WorkflowConcurrencyException('Another unit claimed the same trigger event; retry.');
                }
                if (! in_array($table, self::VERSIONED, true)) {
                    continue;
                }
                $expected = $this->readVersions["{$table}\n{$key}"] ?? null;
                if ($expected !== ($stored?->rowVersion)) {
                    throw new WorkflowConcurrencyException('A '.substr($table, 0, -1).' changed under this unit; retry.');
                }
            }
        }
        foreach ($this->overlay as $table => $rows) {
            foreach ($rows as $key => $row) {
                if ($row === null) {
                    unset($tables[$table][$key]);

                    continue;
                }
                if (in_array($table, self::VERSIONED, true)) {
                    $row->rowVersion = ($this->readVersions["{$table}\n{$key}"] ?? 0) + 1;
                }
                $tables[$table][$key] = $row;
            }
        }
        foreach ($this->newHeads as $tenant => $head) {
            $this->store->auditHeads[$tenant] = $head;
        }
        $this->done = true;
    }
}
