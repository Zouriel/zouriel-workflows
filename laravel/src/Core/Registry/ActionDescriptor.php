<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** An app action `action` steps may call, with typed inputs and outputs. Port of .NET `ActionDescriptor`. */
final readonly class ActionDescriptor
{
    /**
     * @param  list<ParamDescriptor>  $inputs
     * @param  list<ParamDescriptor>  $outputs  read as `steps.<id>.<key>`
     * @param  ?string  $handler  the class the host runs the action with (registered in code; never served)
     */
    public function __construct(
        public string $key = '',
        public string $label = '',
        public ?string $description = null,
        public ?string $category = null,
        public ?string $entity = null,
        public array $inputs = [],
        public array $outputs = [],
        public ?string $handler = null,
    ) {}

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.ActionDescriptor');

        return new self(
            Read::string($value, 'key', $path),
            Read::string($value, 'label', $path),
            Read::optionalString($value, 'description', $path),
            Read::optionalString($value, 'category', $path),
            Read::optionalString($value, 'entity', $path),
            Read::list($value, 'inputs', $path, ParamDescriptor::fromJson(...)) ?? [],
            Read::list($value, 'outputs', $path, ParamDescriptor::fromJson(...)) ?? [],
        );
    }

    public function toJson(bool $withNulls = false): stdClass
    {
        $members = [
            'key' => $this->key, 'label' => $this->label, 'description' => $this->description, 'category' => $this->category,
            'entity' => $this->entity, 'inputs' => array_map(fn (ParamDescriptor $p) => $p->toJson($withNulls), $this->inputs),
            'outputs' => array_map(fn (ParamDescriptor $p) => $p->toJson($withNulls), $this->outputs),
        ];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
