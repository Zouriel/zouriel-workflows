<?php

namespace Zouriel\Workflows\Core\Registry;

use LogicException;

/**
 * Builds a `WorkflowRegistry` in code with a fluent API. Port of .NET `Registry/RegistryBuilder.cs`
 * without the CLR bindings (the Laravel layer binds fields to Eloquent attributes):
 *
 * ```php
 * (new RegistryBuilder)
 *     ->entity('application', fn (EntityBuilder $e) => $e->label('Application', 'Applications')->title('title')
 *         ->field('status', fn (FieldBuilder $f) => $f->enum(ApplicationStatus::class)->lifecycleCapable())
 *         ->relation('programme', 'programme')
 *         ->collection('documents', 'document'))
 *     ->action('finance.createInvoice', CreateInvoice::class, fn (ActionBuilder $a) => $a->label('Create invoice')->for('application')
 *         ->input('amount', 'Amount', 'currency', required: true)->output('invoiceNo', 'Invoice number'))
 *     ->signal('payment.received', fn (SignalBuilder $s) => $s->for('application')->field('amount', 'Amount', 'currency'))
 *     ->role('reviewer', 'Reviewer')
 *     ->build();
 * ```
 */
final class RegistryBuilder
{
    /** @var list<EntityBuilder|EntityDescriptor> */
    private array $entities = [];

    /** @var list<ActionDescriptor> */
    private array $actions = [];

    /** @var list<SignalDescriptor> */
    private array $signals = [];

    /** @var array<string, list<LabelledItem>> */
    private array $lists = ['channels' => [], 'roles' => [], 'groups' => [], 'departments' => [], 'portals' => [], 'queues' => []];

    /**
     * Registers an entity. Only the fields added in `$configure` are visible to workflows.
     *
     * @param  callable(EntityBuilder): mixed|null  $configure
     */
    public function entity(string $key, ?callable $configure = null): self
    {
        $builder = new EntityBuilder($key);
        if ($configure !== null) {
            $configure($builder);
        }
        $this->entities[] = $builder;

        return $this;
    }

    /** Registers an entity described by hand. */
    public function entityDescriptor(EntityDescriptor $descriptor): self
    {
        $this->entities[] = $descriptor;

        return $this;
    }

    /**
     * Registers an app action that `$handler` runs.
     *
     * @param  callable(ActionBuilder): mixed|null  $configure  label, category, record type, inputs and outputs
     */
    public function action(string $key, ?string $handler = null, ?callable $configure = null): self
    {
        $builder = new ActionBuilder($key);
        if ($configure !== null) {
            $configure($builder);
        }
        $this->actions[] = $builder->build($handler);

        return $this;
    }

    /** @param  callable(SignalBuilder): mixed|null  $configure  label, record type and data fields */
    public function signal(string $key, ?callable $configure = null): self
    {
        $builder = new SignalBuilder($key);
        if ($configure !== null) {
            $configure($builder);
        }
        $this->signals[] = $builder->build();

        return $this;
    }

    /** A notification channel. Without any, `email` and `inApp` are offered. */
    public function channel(string $key, string $label): self
    {
        return $this->add('channels', $key, $label);
    }

    public function role(string $key, string $label): self
    {
        return $this->add('roles', $key, $label);
    }

    public function group(string $key, string $label): self
    {
        return $this->add('groups', $key, $label);
    }

    public function department(string $key, string $label): self
    {
        return $this->add('departments', $key, $label);
    }

    public function portal(string $key, string $label): self
    {
        return $this->add('portals', $key, $label);
    }

    public function queue(string $key, string $label): self
    {
        return $this->add('queues', $key, $label);
    }

    /** @throws LogicException an entity or field is registered twice, or a relation names an entity that is not registered */
    public function build(): WorkflowRegistry
    {
        $keys = array_map(fn ($e) => $e->key, $this->entities);
        $entities = array_map(fn ($e) => $e instanceof EntityBuilder ? $e->build($keys) : $e, $this->entities);
        foreach (array_count_values($keys) as $key => $count) {
            if ($count > 1) {
                throw new LogicException("Entity '{$key}' is registered twice.");
            }
        }
        $optional = fn (string $list) => $this->lists[$list] === [] ? null : $this->lists[$list];

        return new WorkflowRegistry(
            entities: $entities,
            actions: $this->actions,
            signals: $this->signals,
            channels: $this->lists['channels'] ?: [new LabelledItem('email', 'Email'), new LabelledItem('inApp', 'In-app')],
            roles: $this->lists['roles'],
            groups: $this->lists['groups'],
            departments: $optional('departments'),
            portals: $optional('portals'),
            queues: $optional('queues'),
        );
    }

    /** A label made from a key or name: `applicantEmail` and `applicant_email` give "Applicant email". */
    public static function humanize(string $name): string
    {
        $spaced = trim(str_replace('_', ' ', preg_replace('/(?<=[a-z0-9])([A-Z])/', ' $1', $name)));

        return $spaced === '' ? $name : mb_strtoupper(mb_substr($spaced, 0, 1)).mb_strtolower(mb_substr($spaced, 1));
    }

    private function add(string $list, string $key, string $label): self
    {
        $this->lists[$list][] = new LabelledItem($key, $label);

        return $this;
    }
}
