<?php

namespace Zouriel\Workflows\Core\Registry;

use LogicException;

/** Describes one entity: its labels, title field and the fields workflows may use. Port of .NET `EntityBuilder<T>`. */
final class EntityBuilder
{
    private ?string $label = null;

    private ?string $plural = null;

    private ?string $title = null;

    /** @var list<array{0: FieldBuilder, 1: ?string}> each field with the related entity of a relation or collection */
    private array $fields = [];

    public function __construct(public readonly string $key) {}

    /** The names shown to people (default: humanised from the key). */
    public function label(string $label, ?string $plural = null): self
    {
        $this->label = $label;
        $this->plural = $plural;

        return $this;
    }

    /** The field shown as the record's name in task lists and audit. */
    public function title(string $field): self
    {
        $this->title = $field;

        return $this;
    }

    /**
     * Adds a field read from the model attribute `$name` (its key by default; text unless a type is set).
     *
     * @param  callable(FieldBuilder): mixed|null  $configure
     */
    public function field(string $name, ?callable $configure = null): self
    {
        return $this->add(new FieldBuilder($name, FieldTypes::TEXT), null, $configure);
    }

    /**
     * Adds a reference to one related record of another registered entity (the field holds its id).
     *
     * @param  callable(FieldBuilder): mixed|null  $configure
     */
    public function relation(string $name, string $entity, ?callable $configure = null): self
    {
        return $this->add(new FieldBuilder($name, FieldTypes::RELATION), $entity, $configure);
    }

    /**
     * Adds a collection of related records of another registered entity (for `forEach`).
     *
     * @param  callable(FieldBuilder): mixed|null  $configure
     */
    public function collection(string $name, string $entity, ?callable $configure = null): self
    {
        return $this->add(new FieldBuilder($name, FieldTypes::COLLECTION), $entity, $configure);
    }

    /** @param  list<string>  $entityKeys  every registered entity key */
    public function build(array $entityKeys): EntityDescriptor
    {
        $fields = array_map(function (array $entry) use ($entityKeys) {
            [$builder, $entity] = $entry;
            if ($entity !== null && ! in_array($entity, $entityKeys, true)) {
                throw new LogicException("'{$this->key}' refers to '{$entity}', which is not a registered workflow entity.");
            }

            return $builder->build($entity);
        }, $this->fields);
        foreach (array_count_values(array_map(fn (FieldDescriptor $f) => $f->key, $fields)) as $key => $count) {
            if ($count > 1) {
                throw new LogicException("Field '{$key}' is registered twice on '{$this->key}'.");
            }
        }

        return new EntityDescriptor($this->key, $this->label ?? RegistryBuilder::humanize($this->key), $this->plural, $this->title, $fields);
    }

    private function add(FieldBuilder $builder, ?string $entity, ?callable $configure): self
    {
        if ($configure !== null) {
            $configure($builder);
        }
        $this->fields[] = [$builder, $entity];

        return $this;
    }
}
