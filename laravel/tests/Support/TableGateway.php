<?php

namespace Zouriel\Workflows\Tests\Support;

use Illuminate\Database\Query\Builder;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Changes\ChangeInspector;
use Zouriel\Workflows\Core\Changes\ChangeKinds;
use Zouriel\Workflows\Core\InvalidOperationException;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\EntityGateway;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Storage\DatabaseSession;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;

/**
 * The spec fixtures' records (declared in JSON, no models) kept in one table of the test database, inspected like
 * `InMemoryGateway` inspects them: the conformance suite then runs every engine fixture against the database store,
 * with record writes in the same transaction as the workflow rows.
 */
final class TableGateway implements EntityGateway
{
    /**
     * @param  array<string, true>  $refused  `entity.field` writes the "database" refuses
     */
    public function __construct(private readonly DatabaseSession $session, private readonly WorkflowRegistry $registry, private readonly ?ChangeInspector $changes,
        private readonly array $refused) {}

    private function query(string $entity, ?string $id = null): Builder
    {
        $q = $this->session->store->table('test_records')->where('entity', $entity);

        return $id === null ? $q : $q->where('id', $id);
    }

    private function values(string $entity, string $id): ?stdClass
    {
        $json = $this->query($entity, $id)->value('json');

        return $json === null ? null : (Json::decode($json) ?? new stdClass);
    }

    public function get(string $entity, string $id): ?RecordSnapshot
    {
        $values = $this->values($entity, $id);

        return $values === null ? null : new RecordSnapshot($entity, $id, $values);
    }

    public function collection(string $entity, string $id, string $field): array
    {
        $owner = $this->get($entity, $id);
        $related = $this->registry->field($entity, $field)?->entity;
        $list = [];
        foreach (is_array($owner?->get($field)) ? $owner->get($field) : [] as $childId) {
            $child = $related === null ? null : $this->get($related, Values::text($childId));
            if ($child !== null) {
                $list[] = $child;
            }
        }

        return $list;
    }

    public function setFields(string $entity, string $id, stdClass $values): void
    {
        $before = $this->values($entity, $id) ?? throw new WorkflowException('noRecord', "{$entity} {$id} does not exist.");
        foreach (get_object_vars($values) as $key => $value) {
            if (isset($this->refused["{$entity}.{$key}"])) {
                throw new InvalidOperationException("The database refused writing {$entity}.{$key}.");
            }
        }
        $after = Json::copy($before);
        foreach (get_object_vars($values) as $key => $value) {
            $after->{$key} = Values::normalize($value);
        }
        $result = $this->changes?->inspect($entity, $id, ChangeKinds::UPDATED, $before, $after);
        $this->session->write();
        $this->query($entity, $id)->update(['json' => Values::serialize($after)]);
        if ($result?->change !== null) {
            $this->session->addOutbox($this->changes->toOutbox($result->change));
        }
    }

    public function create(string $entity, stdClass $values): string
    {
        $id = substr(bin2hex(random_bytes(8)), 0, 12);
        $this->createWithId($entity, $id, $values);

        return $id;
    }

    public function createWithId(string $entity, string $id, stdClass $values): void
    {
        if ($this->values($entity, $id) !== null) {
            throw new WorkflowException('duplicateRecord', "{$entity} {$id} already exists.");
        }
        $after = Values::normalize(Json::copy($values));
        $result = $this->changes?->inspect($entity, $id, ChangeKinds::CREATED, null, $after);
        foreach ($result->initialValues ?? [] as $key => $value) {
            $after->{$key} = $value;
        }
        $this->session->write();
        $this->session->store->table('test_records')->insert(['entity' => $entity, 'id' => $id, 'ord' => DatabaseStore::nextOrder(), 'json' => Values::serialize($after)]);
        if ($result?->change !== null) {
            $this->session->addOutbox($this->changes->toOutbox($result->change));
        }
    }

    public function delete(string $entity, string $id): void
    {
        $before = $this->values($entity, $id) ?? throw new WorkflowException('noRecord', "{$entity} {$id} does not exist.");
        $result = $this->changes?->inspect($entity, $id, ChangeKinds::DELETED, $before, null);
        $this->session->write();
        $this->query($entity, $id)->delete();
        if ($result?->change !== null) {
            $this->session->addOutbox($this->changes->toOutbox($result->change));
        }
    }

    public function listIds(string $entity, ?string $after, int $limit): array
    {
        $q = $this->query($entity)->orderBy('id')->limit($limit);
        if ($after !== null) {
            $q->where('id', '>', $after);
        }

        return $q->pluck('id')->map(fn ($id) => (string) $id)->all();
    }
}
