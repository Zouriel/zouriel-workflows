<?php

namespace Zouriel\Workflows\Core\Storage;

use stdClass;

/** A record's registered field values; relation and user fields hold ids. Port of .NET `RecordSnapshot`. */
final readonly class RecordSnapshot
{
    public function __construct(public string $entity, public string $id, public stdClass $values) {}

    /** The value of a field, or null when it is not in the snapshot. */
    public function get(string $field): mixed
    {
        return $this->values->{$field} ?? null;
    }

    public function has(string $field): bool
    {
        return property_exists($this->values, $field);
    }
}
