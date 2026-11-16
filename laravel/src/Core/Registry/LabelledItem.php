<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A key with a label, for channels, roles, groups, departments, portals and queues. Port of .NET `LabelledItem`. */
final readonly class LabelledItem
{
    public function __construct(public string $key = '', public string $label = '', public ?string $description = null) {}

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.LabelledItem');

        return new self(Read::string($value, 'key', $path), Read::string($value, 'label', $path), Read::optionalString($value, 'description', $path));
    }

    public function toJson(bool $withNulls = false): stdClass
    {
        $members = ['key' => $this->key, 'label' => $this->label, 'description' => $this->description];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
