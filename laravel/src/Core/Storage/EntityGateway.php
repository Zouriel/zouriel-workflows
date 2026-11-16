<?php

namespace Zouriel\Workflows\Core\Storage;

use stdClass;

/** Reads and writes the host app's records by registry key (spec §2). Port of .NET `Storage/Store.cs` `IEntityGateway`. */
interface EntityGateway
{
    /** The record's current registered values, or null when it does not exist. */
    public function get(string $entity, string $id): ?RecordSnapshot;

    /** @return list<RecordSnapshot> the related records of a `collection` field (used by `forEach`) */
    public function collection(string $entity, string $id, string $field): array;

    /** Writes fields on behalf of a workflow; a committed change like any other (spec §7 setFields). */
    public function setFields(string $entity, string $id, stdClass $values): void;

    /** @return list<string> ids in key order after `$after` (null = from the start), at most `$limit` */
    public function listIds(string $entity, ?string $after, int $limit): array;

    /** Creates a record on behalf of a workflow (spec §7 createRecord) and returns its id; `notSupported` when the app does not allow it. */
    public function create(string $entity, stdClass $values): string;

    /** Deletes a record (a reset undoing a record a workflow created); `notSupported` when the app does not allow it. */
    public function delete(string $entity, string $id): void;
}
