<?php

namespace Zouriel\Workflows\Laravel\Changes;

use Illuminate\Database\Eloquent\Model;

/**
 * Lets workflows see a model's writes (spec §5, §6). Its `creating`/`updating`/`deleting` hooks fill empty lifecycle
 * fields with their initial state and refuse writes to fields a lifecycle owns (enforce mode); its
 * `created`/`updated`/`deleted` hooks add the change event to the outbox. `save()`, `delete()` and
 * `increment()`/`decrement()` run in a transaction when none is open, so the record and its event always commit
 * together, and workflows start only for changes that really committed. Port of .NET `WorkflowSaveChangesInterceptor`.
 *
 * Writes that fire no model events are not seen: `Model::query()->update()`, `DB::table()`, `saveQuietly()`,
 * `withoutEvents()`, `upsert()`. Report deliberate bulk changes with `ModelChanges::record()`.
 *
 * @mixin Model
 */
trait HasWorkflows
{
    public static function bootHasWorkflows(): void
    {
        foreach (['creating', 'created', 'updating', 'updated', 'deleting', 'deleted'] as $event) {
            static::registerModelEvent($event, fn (Model $model) => app(ModelChanges::class)->handle($event, $model));
        }
    }

    public function save(array $options = []): bool
    {
        return ModelChanges::atomically($this, fn () => parent::save($options));
    }

    public function delete()
    {
        return ModelChanges::atomically($this, fn () => parent::delete());
    }

    protected function incrementOrDecrement($column, $amount, $extra, $method)
    {
        return ModelChanges::atomically($this, fn () => parent::incrementOrDecrement($column, $amount, $extra, $method));
    }

    protected function incrementOrDecrementEach(array $columns, array $extra, string $method)
    {
        return ModelChanges::atomically($this, fn () => parent::incrementOrDecrementEach($columns, $extra, $method));
    }
}
