<?php

namespace Zouriel\Workflows\Laravel\Changes;

use Closure;
use Illuminate\Contracts\Container\Container;
use Illuminate\Database\Eloquent\Model;
use LogicException;
use stdClass;
use WeakMap;
use Zouriel\Workflows\Core\Changes\ChangeKinds;
use Zouriel\Workflows\Core\Changes\EntityChange;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Laravel\Eloquent\ModelValues;
use Zouriel\Workflows\Laravel\Registry\ModelBindings;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;

/**
 * The change hook behind `HasWorkflows`. Before a write it asks the engine's `ChangeInspector` (initial lifecycle
 * states are set on the model, a write to an owned field throws `FieldOwnedException` and nothing is saved); after the
 * write it adds the change event to the outbox on the same connection, inside the write's transaction. Events are kept
 * per model between the two hooks, so a write that fails in between leaves no event behind. Port of .NET
 * `WorkflowSaveChangesInterceptor`.
 */
final class ModelChanges
{
    /** @var WeakMap<Model, EntityChange|null> the event prepared before each write, added after it succeeds */
    private WeakMap $pending;

    public function __construct(private readonly Container $app)
    {
        $this->pending = new WeakMap;
    }

    /**
     * Runs a model write in a transaction when none is open, so the record and its change event commit together.
     *
     * @template T
     *
     * @param  Closure(): T  $write
     * @return T
     */
    public static function atomically(Model $model, Closure $write): mixed
    {
        $db = $model->getConnection();

        return $db->transactionLevel() > 0 ? $write() : $db->transaction($write);
    }

    private function engine(): WorkflowEngine
    {
        return $this->app->make(WorkflowEngine::class);
    }

    public function handle(string $event, Model $model): void
    {
        $entity = $this->app->make(ModelBindings::class)->entityOf($model);
        if ($entity === null) {
            return;
        }
        $descriptor = $this->engine()->registry->entity($entity);
        if ($descriptor === null) {
            return;
        }
        match ($event) {
            'creating' => $this->before($model, function () use ($model, $entity, $descriptor) {
                $after = ModelValues::tracked($descriptor, $model, original: false);
                // Inspected with a placeholder id: the database gives the id on insert; the event gets it afterwards.
                $result = $this->engine()->changes->inspect($entity, '(new)', ChangeKinds::CREATED, null, $after);
                foreach ($result->initialValues as $field => $value) {
                    ModelValues::write($model, $descriptor->field($field), $value);
                }

                return $result->change;
            }),
            'updating' => $this->before($model, fn () => $this->engine()->changes->inspect($entity, Values::text($model->getKey()), ChangeKinds::UPDATED,
                ModelValues::tracked($descriptor, $model, original: true), ModelValues::tracked($descriptor, $model, original: false))->change),
            'deleting' => $this->before($model, fn () => $this->engine()->changes->inspect($entity, Values::text($model->getKey()), ChangeKinds::DELETED,
                ModelValues::tracked($descriptor, $model, original: true), null)->change),
            'created', 'updated', 'deleted' => $this->after($model),
            default => null,
        };
    }

    /** @param  Closure(): ?EntityChange  $inspect */
    private function before(Model $model, Closure $inspect): void
    {
        if ($model->getConnection()->transactionLevel() === 0) {
            throw new LogicException($model::class.' is written outside a transaction, so its workflow change event could not commit with it. '
                .'Write it with save(), delete() or increment() (HasWorkflows opens a transaction), or inside DB::transaction().');
        }
        unset($this->pending[$model]);
        $this->pending[$model] = $inspect();
    }

    private function after(Model $model): void
    {
        if (! isset($this->pending[$model])) {
            return;
        }
        $change = $this->pending[$model];
        unset($this->pending[$model]);
        if ($change->kind === ChangeKinds::CREATED) {
            $change->entityId = Values::text($model->getKey());
        }
        $this->app->make(DatabaseStore::class)->insertOutbox($this->engine()->changes->toOutbox($change));
    }

    /**
     * Reports a change made without model events (a bulk update, raw SQL) so workflows see it like any other. Call it
     * inside the transaction that made the change. Writes to owned fields are refused (`FieldOwnedException`).
     */
    public function record(string $entity, string $id, string $kind, ?stdClass $before, ?stdClass $after): void
    {
        $change = $this->engine()->changes->inspect($entity, $id, $kind, $before, $after)->change;
        if ($change !== null) {
            $this->app->make(DatabaseStore::class)->insertOutbox($this->engine()->changes->toOutbox($change));
        }
    }
}
