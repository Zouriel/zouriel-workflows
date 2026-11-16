<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Workflows\Core\InvalidOperationException;
use Zouriel\Workflows\Core\Storage\EntityGateway;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\Storage\WorkflowStore;

/** Reads the app's records through one short unit of work per read; test runs never write to them. Port of .NET `Simulate.cs` `PerReadGateway`. */
final class ReadOnlyGateway implements EntityGateway
{
    public function __construct(private readonly WorkflowStore $store) {}

    public function get(string $entity, string $id): ?RecordSnapshot
    {
        return $this->store->begin()->entities()->get($entity, $id);
    }

    public function collection(string $entity, string $id, string $field): array
    {
        return $this->store->begin()->entities()->collection($entity, $id, $field);
    }

    public function listIds(string $entity, ?string $after, int $limit): array
    {
        return $this->store->begin()->entities()->listIds($entity, $after, $limit);
    }

    public function setFields(string $entity, string $id, stdClass $values): void
    {
        throw new InvalidOperationException("Test runs never write to the app's records.");
    }

    public function create(string $entity, stdClass $values): string
    {
        throw new InvalidOperationException("Test runs never write to the app's records.");
    }

    public function delete(string $entity, string $id): void
    {
        throw new InvalidOperationException("Test runs never write to the app's records.");
    }
}
