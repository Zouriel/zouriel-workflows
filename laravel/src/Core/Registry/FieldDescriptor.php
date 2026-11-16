<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Json\Write;

/** A field of an entity and what workflows may do with it (spec §2 flags). Port of .NET `FieldDescriptor`. */
final readonly class FieldDescriptor
{
    /**
     * @param  list<FieldOption>|null  $options  the options of an enum field
     * @param  ?string  $entity  for relations and collections: the related entity's key
     * @param  ?string  $attribute  the host model's attribute the field reads (registered in code; never served)
     */
    public function __construct(
        public string $key = '',
        public string $label = '',
        public string $type = FieldTypes::TEXT,
        public ?array $options = null,
        public ?string $entity = null,
        public ?bool $readable = null,
        public ?bool $watchable = null,
        public ?bool $settable = null,
        public ?bool $lifecycle = null,
        public ?bool $sensitive = null,
        public ?bool $required = null,
        public ?string $attribute = null,
    ) {}

    /** True unless `readable` is false. */
    public function isReadable(): bool
    {
        return $this->readable !== false;
    }

    public function isWatchable(): bool
    {
        return $this->watchable === true;
    }

    public function isSettable(): bool
    {
        return $this->settable === true;
    }

    /** True when a lifecycle may own the field (flagged and an enum). */
    public function isLifecycle(): bool
    {
        return $this->lifecycle === true && $this->type === FieldTypes::ENUM;
    }

    public function isSensitive(): bool
    {
        return $this->sensitive === true;
    }

    /** The label of an enum value, or the value as text when it is not an option. */
    public function optionLabel(mixed $value): string
    {
        $text = Values::text($value);
        if ($this->options === null || $value === null) {
            return $text;
        }

        return array_find($this->options, fn (FieldOption $o) => Values::text($o->value) === $text)?->label ?? $text;
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.FieldDescriptor');

        return new self(
            key: Read::string($value, 'key', $path),
            label: Read::string($value, 'label', $path),
            type: Read::string($value, 'type', $path, FieldTypes::TEXT),
            // A null option value reads as "" (.NET `WorkflowRegistry.FromJson` normalises field options).
            options: Read::list($value, 'options', $path, fn ($o, $p) => ($option = FieldOption::fromJson($o, $p))->value === null ? new FieldOption('', $option->label) : $option),
            entity: Read::optionalString($value, 'entity', $path),
            readable: Read::optionalBool($value, 'readable', $path),
            watchable: Read::optionalBool($value, 'watchable', $path),
            settable: Read::optionalBool($value, 'settable', $path),
            lifecycle: Read::optionalBool($value, 'lifecycle', $path),
            sensitive: Read::optionalBool($value, 'sensitive', $path),
            required: Read::optionalBool($value, 'required', $path),
        );
    }

    public function toJson(bool $withNulls = false): stdClass
    {
        $members = [
            'key' => $this->key, 'label' => $this->label, 'type' => $this->type, 'options' => Write::list($this->options),
            'entity' => $this->entity, 'readable' => $this->readable, 'watchable' => $this->watchable, 'settable' => $this->settable,
            'lifecycle' => $this->lifecycle, 'sensitive' => $this->sensitive, 'required' => $this->required,
        ];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
