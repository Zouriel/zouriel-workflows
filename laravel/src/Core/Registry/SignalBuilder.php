<?php

namespace Zouriel\Workflows\Core\Registry;

/** Describes a signal: its label, record type and data fields. Port of .NET `SignalBuilder`. */
final class SignalBuilder
{
    private ?string $label = null;

    private ?string $entity = null;

    /** @var list<ParamDescriptor> */
    private array $fields = [];

    public function __construct(private readonly string $key) {}

    /** The name shown to people (default: humanised from the last part of the key). */
    public function label(string $label): self
    {
        $this->label = $label;

        return $this;
    }

    /** The record type the signal is about. */
    public function for(string $entity): self
    {
        $this->entity = $entity;

        return $this;
    }

    /** A data field, read as `trigger.data.<key>`. */
    public function field(string $key, string $label, string $type = FieldTypes::TEXT): self
    {
        $this->fields[] = new ParamDescriptor($key, $label, $type);

        return $this;
    }

    public function build(): SignalDescriptor
    {
        $parts = explode('.', $this->key);

        return new SignalDescriptor($this->key, $this->label ?? RegistryBuilder::humanize(end($parts)), $this->entity, $this->fields);
    }
}
