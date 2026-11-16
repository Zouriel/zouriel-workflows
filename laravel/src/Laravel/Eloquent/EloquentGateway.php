<?php

namespace Zouriel\Workflows\Laravel\Eloquent;

use Illuminate\Database\Eloquent\Builder;
use Illuminate\Database\Eloquent\Model;
use stdClass;
use Throwable;
use Zouriel\Workflows\Core\Registry\EntityDescriptor;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\EntityGateway;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Registry\ModelBindings;
use Zouriel\Workflows\Laravel\Storage\DatabaseSession;

/**
 * Reads and writes the app's records through their Eloquent models, inside the unit's transaction: a workflow's
 * writes are ordinary model saves, so the change hook (`HasWorkflows`) sees them under the engine's write scope and
 * they commit or roll back with the workflow. Each write runs in a savepoint, so a write the database refuses fails the
 * step without spoiling the unit. Port of .NET `EfEntityGateway`.
 *
 * Records are found without global scopes (a worker has no signed-in user for scopes to read), except that soft-deleted
 * records do not exist for workflows.
 */
final class EloquentGateway implements EntityGateway
{
    public function __construct(private readonly DatabaseSession $session, private readonly WorkflowRegistry $registry, private readonly ModelBindings $models) {}

    private function entity(string $key): EntityDescriptor
    {
        return $this->registry->entity($key) ?? throw new WorkflowException('unknownEntity', "'{$key}' is not a registered workflow entity.");
    }

    /** @return Builder<Model> */
    private function query(string $entity): Builder
    {
        $class = $this->models->modelOf($entity);
        $model = new $class;
        $query = $model->newQueryWithoutScopes();
        if (method_exists($model, 'getQualifiedDeletedAtColumn')) {
            $query->whereNull($model->getQualifiedDeletedAtColumn());
        }

        return $query;
    }

    /** The record, or null when it does not exist or `$id` cannot be one of this model's keys (no query is sent then). */
    private function find(string $entity, string $id): ?Model
    {
        $class = $this->models->modelOf($entity);
        $model = new $class;
        if (in_array($model->getKeyType(), ['int', 'integer'], true) && preg_match('/^-?\d+$/', $id) !== 1) {
            return null;
        }
        // A string that is not a UUID/ULID would make PostgreSQL fail the query (and with it the unit's transaction).
        if (method_exists($model, 'usesUniqueIds') && $model->usesUniqueIds() && method_exists($model, 'isValidUniqueId')
            && ! (fn (string $value) => $this->isValidUniqueId($value))->call($model, $id)) {
            return null;
        }

        return $this->query($entity)->where($model->getKeyName(), $id)->first();
    }

    public function get(string $entity, string $id): ?RecordSnapshot
    {
        $model = $this->find($entity, $id);

        return $model === null ? null : new RecordSnapshot($entity, $id, ModelValues::snapshot($this->entity($entity), $model));
    }

    public function collection(string $entity, string $id, string $field): array
    {
        $f = $this->entity($entity)->field($field);
        $model = $this->find($entity, $id);
        if ($model === null || $f?->type !== FieldTypes::COLLECTION || $f->entity === null) {
            return [];
        }
        $relation = ModelValues::relation($model, ModelValues::attribute($f)) ?? throw new WorkflowException('unknownField', "'{$field}' is not a relation of {$entity}.");
        $child = $this->entity($f->entity);

        return $relation->get()->map(fn (Model $item) => new RecordSnapshot($f->entity, (string) $item->getKey(), ModelValues::snapshot($child, $item)))->values()->all();
    }

    public function setFields(string $entity, string $id, stdClass $values): void
    {
        $e = $this->entity($entity);
        $model = $this->find($entity, $id) ?? throw new WorkflowException('noRecord', "{$e->label} {$id} does not exist.");
        foreach (get_object_vars($values) as $key => $value) {
            $f = $e->field((string) $key) ?? throw new WorkflowException('unknownField', "'{$key}' is not a field of {$e->label}.");
            ModelValues::write($model, $f, $value);
        }
        $this->saved(fn () => $model->save());
    }

    public function create(string $entity, stdClass $values): string
    {
        $e = $this->entity($entity);
        $class = $this->models->modelOf($entity);
        try {
            $model = new $class;
        } catch (Throwable) {
            throw new WorkflowException('notSupported', "{$e->label} records cannot be created by workflows.");
        }
        foreach (get_object_vars($values) as $key => $value) {
            $f = $e->field((string) $key) ?? throw new WorkflowException('unknownField', "'{$key}' is not a field of {$e->label}.");
            ModelValues::write($model, $f, $value);
        }
        $this->saved(fn () => $model->save());

        return (string) $model->getKey();
    }

    public function delete(string $entity, string $id): void
    {
        $e = $this->entity($entity);
        $model = $this->find($entity, $id) ?? throw new WorkflowException('noRecord', "{$e->label} {$id} does not exist.");
        $this->saved(fn () => $model->delete());
    }

    public function listIds(string $entity, ?string $after, int $limit): array
    {
        $class = $this->models->modelOf($entity);
        $model = new $class;
        $key = $model->getKeyName();
        $numeric = in_array($model->getKeyType(), ['int', 'integer'], true);
        $query = $this->query($entity)->orderBy($key)->limit($limit);
        if ($after !== null) {
            $query->where($key, '>', $numeric ? (int) $after : $after);
        }

        return $query->pluck($key)->map(fn ($id) => (string) $id)->all();
    }

    /** Runs a record write inside the unit's transaction, in a savepoint of its own (Laravel nests transactions as savepoints). */
    private function saved(callable $write): void
    {
        $this->session->write();
        $this->session->store->db->transaction(fn () => $write());
    }
}
