<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** An input or output of an action, or a data field of a signal. Port of .NET `ParamDescriptor`. */
final readonly class ParamDescriptor
{
    /** @param  list<FieldOption>|null  $options */
    public function __construct(
        public string $key = '',
        public string $label = '',
        public string $type = FieldTypes::TEXT,
        public ?bool $required = null,
        public ?array $options = null,
        public ?string $entity = null,
        public ?string $description = null,
    ) {}

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.ParamDescriptor');

        return new self(
            Read::string($value, 'key', $path),
            Read::string($value, 'label', $path),
            Read::string($value, 'type', $path, FieldTypes::TEXT),
            Read::optionalBool($value, 'required', $path),
            Read::list($value, 'options', $path, FieldOption::fromJson(...)),
            Read::optionalString($value, 'entity', $path),
            Read::optionalString($value, 'description', $path),
        );
    }

    public function toJson(bool $withNulls = false): stdClass
    {
        $members = [
            'key' => $this->key, 'label' => $this->label, 'type' => $this->type, 'required' => $this->required,
            'options' => Write::list($this->options), 'entity' => $this->entity, 'description' => $this->description,
        ];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
