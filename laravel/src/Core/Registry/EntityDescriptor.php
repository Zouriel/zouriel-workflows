<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A record type the app registered, with the fields workflows may use. Port of .NET `EntityDescriptor`. */
final readonly class EntityDescriptor
{
    /** @param  list<FieldDescriptor>  $fields */
    public function __construct(
        public string $key = '',
        public string $label = '',
        public ?string $pluralLabel = null,
        public ?string $titleField = null,
        public array $fields = [],
    ) {}

    public function field(?string $key): ?FieldDescriptor
    {
        return $key === null ? null : array_find($this->fields, fn (FieldDescriptor $f) => $f->key === $key);
    }

    /** A copy with other fields (the public registry leaves some out). */
    public function withFields(array $fields): self
    {
        return new self($this->key, $this->label, $this->pluralLabel, $this->titleField, $fields);
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.EntityDescriptor');

        return new self(
            Read::string($value, 'key', $path),
            Read::string($value, 'label', $path),
            Read::optionalString($value, 'pluralLabel', $path),
            Read::optionalString($value, 'titleField', $path),
            Read::list($value, 'fields', $path, FieldDescriptor::fromJson(...)) ?? [],
        );
    }

    public function toJson(bool $withNulls = false): stdClass
    {
        $members = [
            'key' => $this->key, 'label' => $this->label, 'pluralLabel' => $this->pluralLabel, 'titleField' => $this->titleField,
            'fields' => array_map(fn (FieldDescriptor $f) => $f->toJson($withNulls), $this->fields),
        ];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
