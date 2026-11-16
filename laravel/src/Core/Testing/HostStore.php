<?php

namespace Zouriel\Workflows\Core\Testing;

use stdClass;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\Storage\WorkflowStore;

/**
 * A store the test host and the conformance runner can drive: it holds records the way the app would (writes inspected
 * for change events and owned fields), and shows its committed rows. `InMemoryStore` is one; the Laravel tests run the
 * same fixtures against the database store through another.
 */
interface HostStore extends WorkflowStore
{
    /** Connects the store to the engine whose change inspector its record writes go through. */
    public function attach(WorkflowEngine $engine): void;

    /** Creates a record the way the app would (change hook and created event included). */
    public function createRecord(string $entity, string $id, stdClass $values): void;

    /** Changes fields of a record the way the app would (ownership enforced, change event written). */
    public function changeRecord(string $entity, string $id, stdClass $values): void;

    /** Deletes a record the way the app would (deleted event with its last values). */
    public function deleteRecord(string $entity, string $id): void;

    /** A record's committed values, read outside any unit, or null. */
    public function peekRecord(string $entity, string $id): ?RecordSnapshot;

    /**
     * Copies of every committed row of a workflow table (`instances`, `tasks`, `bookmarks`, `outbox`, `audit`...), as
     * the store's record classes.
     *
     * @return list<object>
     */
    public function all(string $table): array;

    /** Makes the store refuse (or accept again) writes to `entity.field`, as a database constraint of the app would. */
    public function refuseWrites(string $field, bool $refuse): void;
}
