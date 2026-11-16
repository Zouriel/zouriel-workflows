<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A named event the app raises, optionally about one record type. Port of .NET `SignalDescriptor`. */
final readonly class SignalDescriptor
{
    /** @param  list<ParamDescriptor>  $fields  its data (`trigger.data.*`, wait output `data`) */
    public function __construct(public string $key = '', public string $label = '', public ?string $entity = null, public array $fields = []) {}

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.SignalDescriptor');

        return new self(
            Read::string($value, 'key', $path),
            Read::string($value, 'label', $path),
            Read::optionalString($value, 'entity', $path),
            Read::list($value, 'fields', $path, ParamDescriptor::fromJson(...)) ?? [],
        );
    }

    public function toJson(bool $withNulls = false): stdClass
    {
        $members = [
            'key' => $this->key, 'label' => $this->label, 'entity' => $this->entity,
            'fields' => array_map(fn (ParamDescriptor $p) => $p->toJson($withNulls), $this->fields),
        ];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
