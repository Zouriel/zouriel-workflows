<?php

namespace Zouriel\Workflows\Laravel\Registry;

use Illuminate\Database\Eloquent\Model;
use LogicException;
use Zouriel\Workflows\Core\Registry\ActionBuilder;
use Zouriel\Workflows\Core\Registry\EntityBuilder;
use Zouriel\Workflows\Core\Registry\RegistryBuilder;
use Zouriel\Workflows\Core\Registry\SignalBuilder;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Laravel\Changes\HasWorkflows;

/**
 * The registry builder of a Laravel app: `RegistryBuilder` with each entity bound to an Eloquent model. Field names are
 * the model's attributes (`status`), a relation method for `relation` and `collection` fields (`programme`,
 * `documents`), or a dotted path through relations for read-only fields (`programme.name`). Spec §2.
 *
 * ```php
 * $registry
 *     ->entity('application', Application::class, fn (EntityBuilder $e) => $e->label('Application', 'Applications')->title('title')
 *         ->field('status', fn (FieldBuilder $f) => $f->enum(ApplicationStatus::class)->lifecycleCapable())
 *         ->field('amount', fn (FieldBuilder $f) => $f->currency()->watchable()->settable())
 *         ->relation('programme', 'programme')
 *         ->collection('documents', 'document'))
 *     ->action('finance.createInvoice', CreateInvoice::class, fn (ActionBuilder $a) => $a->label('Create invoice')->for('application'))
 *     ->role('reviewer', 'Reviewer');
 * ```
 */
final class ModelRegistry
{
    private readonly RegistryBuilder $builder;

    /** @var array<string, class-string<Model>> */
    private array $models = [];

    public function __construct()
    {
        $this->builder = new RegistryBuilder;
    }

    /**
     * Registers an entity stored by `$model`, whose writes are watched for change events (the model uses `HasWorkflows`).
     *
     * @param  class-string<Model>  $model
     * @param  callable(EntityBuilder): mixed|null  $configure
     */
    public function entity(string $key, string $model, ?callable $configure = null): self
    {
        if (! is_subclass_of($model, Model::class)) {
            throw new LogicException("Workflow entity '{$key}' must be an Eloquent model; {$model} is not.");
        }
        if (! in_array(HasWorkflows::class, class_uses_recursive($model), true)) {
            throw new LogicException("{$model} is a workflow entity ('{$key}') but does not use ".HasWorkflows::class
                .', so its changes would never reach workflows. Add the trait to the model.');
        }
        $this->builder->entity($key, $configure);
        $this->models[$key] = $model;

        return $this;
    }

    /** @param  callable(ActionBuilder): mixed|null  $configure */
    public function action(string $key, ?string $handler = null, ?callable $configure = null): self
    {
        $this->builder->action($key, $handler, $configure);

        return $this;
    }

    /** @param  callable(SignalBuilder): mixed|null  $configure */
    public function signal(string $key, ?callable $configure = null): self
    {
        $this->builder->signal($key, $configure);

        return $this;
    }

    public function channel(string $key, string $label): self
    {
        $this->builder->channel($key, $label);

        return $this;
    }

    public function role(string $key, string $label): self
    {
        $this->builder->role($key, $label);

        return $this;
    }

    public function group(string $key, string $label): self
    {
        $this->builder->group($key, $label);

        return $this;
    }

    public function department(string $key, string $label): self
    {
        $this->builder->department($key, $label);

        return $this;
    }

    public function portal(string $key, string $label): self
    {
        $this->builder->portal($key, $label);

        return $this;
    }

    public function queue(string $key, string $label): self
    {
        $this->builder->queue($key, $label);

        return $this;
    }

    public function build(): WorkflowRegistry
    {
        return $this->builder->build();
    }

    public function bindings(): ModelBindings
    {
        return new ModelBindings($this->models);
    }
}
