<?php

namespace Zouriel\Workflows\Core\Storage;

use Zouriel\Workflows\Core\Changes\ChangeInspector;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Time\Clock;
use Zouriel\Workflows\Core\Time\SystemClock;

/**
 * A complete store kept in memory, with real units of work: a session sees its own writes, and nothing
 * reaches the shared data until `commit()`, which checks row versions, live keys, trigger claims and the
 * audit chain head like a database would. Used by conformance tests, test runs (`/simulate`) and demos.
 * It also holds records, so it doubles as the entity gateway. Port of .NET `Storage/InMemoryWorkflowStore.cs`.
 */
final class InMemoryStore implements WorkflowStore
{
    /** @var array<string, array<string, object>> committed rows by table and key, in insertion order */
    public array $tables = [];

    /** @var array<string, AuditHead> the head of each tenant's audit chain (key: tenant, or "" without tenancy) */
    public array $auditHeads = [];

    public int $recordOrder = 0;

    /** Set after the engine is created, so record writes are inspected (change events, owned fields). */
    public ?ChangeInspector $changes = null;

    /**
     * Fields (`entity.field`) whose writes this store refuses, as a database constraint of the app would;
     * tests use it to make workflow writes (and the timers that make them) fail.
     *
     * @var array<string, true>
     */
    public array $refusedWrites = [];

    /** Reads records that are not in memory from here (test runs read the real app's records). */
    public ?EntityGateway $fallback = null;

    /** The clock for housekeeping times (trigger claims). */
    public Clock $clock;

    /** The registry, for resolving collection fields to their entity. */
    public ?WorkflowRegistry $registry = null;

    public function __construct(?WorkflowRegistry $registry = null, ?Clock $clock = null)
    {
        $this->registry = $registry;
        $this->clock = $clock ?? new SystemClock;
    }

    public function begin(): InMemorySession
    {
        return new InMemorySession($this);
    }

    /** Creates a record the way the app would (change hook and created event included). */
    public function createRecord(string $entity, string $id, \stdClass $values): void
    {
        $session = $this->begin();
        $session->records->createWithId($entity, $id, $values);
        $session->commit();
    }

    /** Changes fields of a record the way the app would (ownership enforced, change event written). */
    public function changeRecord(string $entity, string $id, \stdClass $values): void
    {
        $session = $this->begin();
        $session->records->setFields($entity, $id, $values);
        $session->commit();
    }

    /** Deletes a record the way the app would (deleted event with its last values). */
    public function deleteRecord(string $entity, string $id): void
    {
        $session = $this->begin();
        $session->records->delete($entity, $id);
        $session->commit();
    }

    /** A record's committed values, read outside any unit (for tests), or null. */
    public function peekRecord(string $entity, string $id): ?RecordSnapshot
    {
        $row = $this->tables['records']["{$entity}:{$id}"] ?? null;

        return $row instanceof StoredRecord ? $row->snapshot() : null;
    }

    /**
     * Copies of every committed row of a table (for tests), in insertion order.
     *
     * @return list<object>
     */
    public function all(string $table): array
    {
        return array_values(array_map(fn (object $row) => clone $row, $this->tables[$table] ?? []));
    }
}
