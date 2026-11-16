<?php

namespace Zouriel\Workflows\Core\Storage;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Changes\ChangeKinds;
use Zouriel\Workflows\Core\InvalidOperationException;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\WorkflowException;

/** Records kept in memory, inspected as a database hook would (change events, owned fields). Port of .NET `InMemoryWorkflowStore.Gateway`. */
final class InMemoryGateway implements EntityGateway
{
    public function __construct(private readonly InMemorySession $session) {}

    private static function key(string $entity, string $id): string
    {
        return "{$entity}:{$id}";
    }

    private function stored(string $entity, string $id): ?StoredRecord
    {
        return $this->session->get('records', self::key($entity, $id));
    }

    public function get(string $entity, string $id): ?RecordSnapshot
    {
        return $this->stored($entity, $id)?->snapshot() ?? $this->session->store->fallback?->get($entity, $id);
    }

    public function collection(string $entity, string $id, string $field): array
    {
        $owner = $this->get($entity, $id);
        if ($owner === null) {
            return [];
        }
        if ($this->stored($entity, $id) === null && $this->session->store->fallback !== null) {
            return $this->session->store->fallback->collection($entity, $id, $field);
        }
        $related = $this->session->store->registry?->field($entity, $field)?->entity;
        $list = [];
        foreach (is_array($owner->get($field)) ? $owner->get($field) : [] as $childId) {
            $child = $related === null ? null : $this->get($related, Values::text($childId));
            if ($child !== null) {
                $list[] = $child;
            }
        }

        return $list;
    }

    public function setFields(string $entity, string $id, stdClass $values): void
    {
        $existing = $this->stored($entity, $id);
        $before = $existing?->snapshot() ?? $this->session->store->fallback?->get($entity, $id);
        if ($before === null) {
            throw new WorkflowException('noRecord', "{$entity} {$id} does not exist.");
        }
        foreach (get_object_vars($values) as $key => $value) {
            if (isset($this->session->store->refusedWrites["{$entity}.{$key}"])) {
                throw new InvalidOperationException("The database refused writing {$entity}.{$key}.");
            }
        }
        $after = Json::copy($before->values);
        foreach (get_object_vars($values) as $key => $value) {
            $after->{$key} = Values::normalize($value);
        }
        $result = $this->session->store->changes?->inspect($entity, $id, ChangeKinds::UPDATED, $before->values, $after);
        $this->session->put('records', self::key($entity, $id), new StoredRecord($entity, $id, $existing->order ?? ++$this->session->store->recordOrder, Values::serialize($after)));
        if ($result?->change !== null) {
            $this->session->addOutbox($this->session->store->changes->toOutbox($result->change));
        }
    }

    public function create(string $entity, stdClass $values): string
    {
        $id = substr(bin2hex(random_bytes(8)), 0, 12);
        $this->createWithId($entity, $id, $values);

        return $id;
    }

    /** Creates a record with a known id (the app creating one). */
    public function createWithId(string $entity, string $id, stdClass $values): void
    {
        if ($this->stored($entity, $id) !== null) {
            throw new WorkflowException('duplicateRecord', "{$entity} {$id} already exists.");
        }
        $after = Values::normalize(Json::copy($values));
        $result = $this->session->store->changes?->inspect($entity, $id, ChangeKinds::CREATED, null, $after);
        foreach ($result->initialValues ?? [] as $key => $value) {
            $after->{$key} = $value;
        }
        $this->session->put('records', self::key($entity, $id), new StoredRecord($entity, $id, ++$this->session->store->recordOrder, Values::serialize($after)));
        if ($result?->change !== null) {
            $this->session->addOutbox($this->session->store->changes->toOutbox($result->change));
        }
    }

    public function delete(string $entity, string $id): void
    {
        $existing = $this->stored($entity, $id) ?? throw new WorkflowException('noRecord', "{$entity} {$id} does not exist.");
        $result = $this->session->store->changes?->inspect($entity, $id, ChangeKinds::DELETED, $existing->snapshot()->values, null);
        $this->session->remove('records', self::key($entity, $id));
        if ($result?->change !== null) {
            $this->session->addOutbox($this->session->store->changes->toOutbox($result->change));
        }
    }

    public function listIds(string $entity, ?string $after, int $limit): array
    {
        $ids = array_map(fn (StoredRecord $r) => $r->id, array_filter($this->session->all('records'), fn (StoredRecord $r) => $r->entity === $entity
            && ($after === null || strcmp($r->id, $after) > 0)));
        usort($ids, strcmp(...));

        return array_slice($ids, 0, $limit);
    }
}
