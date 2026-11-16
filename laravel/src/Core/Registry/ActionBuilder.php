<?php

namespace Zouriel\Workflows\Core\Registry;

/** Describes an app action: its label, category, record type, inputs and outputs. Port of .NET `ActionBuilder`. */
final class ActionBuilder
{
    private ?string $label = null;

    private ?string $description = null;

    private ?string $category = null;

    private ?string $entity = null;

    /** @var list<ParamDescriptor> */
    private array $inputs = [];

    /** @var list<ParamDescriptor> */
    private array $outputs = [];

    public function __construct(private readonly string $key) {}

    /** The name shown to people (default: humanised from the last part of the key). */
    public function label(string $label): self
    {
        $this->label = $label;

        return $this;
    }

    public function description(string $text): self
    {
        $this->description = $text;

        return $this;
    }

    /** The group the action appears under in the step picker. */
    public function category(string $category): self
    {
        $this->category = $category;

        return $this;
    }

    /** The entity the action works on (the workflow's record is passed to it). */
    public function for(string $entity): self
    {
        $this->entity = $entity;

        return $this;
    }

    public function input(string $key, string $label, string $type = FieldTypes::TEXT, bool $required = false, ?string $description = null): self
    {
        $this->inputs[] = new ParamDescriptor($key, $label, $type, $required ? true : null, description: $description);

        return $this;
    }

    /** An output, read as `steps.<id>.<key>`. */
    public function output(string $key, string $label, string $type = FieldTypes::TEXT): self
    {
        $this->outputs[] = new ParamDescriptor($key, $label, $type);

        return $this;
    }

    public function build(?string $handler): ActionDescriptor
    {
        $parts = explode('.', $this->key);

        return new ActionDescriptor(
            $this->key, $this->label ?? RegistryBuilder::humanize(end($parts)), $this->description, $this->category, $this->entity,
            $this->inputs, $this->outputs, $handler,
        );
    }
}
