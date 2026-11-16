<?php

namespace Zouriel\Workflows\Core\Storage;

use Zouriel\Forms\Json;

/** A record held by the in-memory store: its values as JSON text, so every read is a fresh copy. */
final class StoredRecord
{
    public function __construct(public string $entity, public string $id, public int $order, public string $json) {}

    public function snapshot(): RecordSnapshot
    {
        return new RecordSnapshot($this->entity, $this->id, Json::decode($this->json) ?? new \stdClass);
    }
}
