<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\Read;

/** One option of an enum field: the stored value and the label people see. Port of .NET `FieldOption`. */
final readonly class FieldOption
{
    /** The stored value, as a workflow value. */
    public mixed $value;

    public function __construct(mixed $value = '', public string $label = '')
    {
        $this->value = Json::normalize($value);
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Registry.FieldOption');

        return new self(property_exists($value, 'value') ? $value->value : '', Read::string($value, 'label', $path));
    }

    public function toJson(): stdClass
    {
        return (object) ['value' => $this->value, 'label' => $this->label];
    }
}
