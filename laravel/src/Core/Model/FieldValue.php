<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** One field a `setFields` or `createRecord` step writes. Port of .NET `FieldValue`. */
final class FieldValue
{
    public string $field = '';

    public Operand $value;

    public function __construct()
    {
        $this->value = Operand::of(null);
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.FieldValue');
        $item = new self;
        $item->field = Read::string($value, 'field', $path);
        if (($value->value ?? null) !== null) {
            $item->value = Operand::fromJson($value->value, "{$path}.value");
        }

        return $item;
    }

    public function toJson(): stdClass
    {
        return (object) ['field' => $this->field, 'value' => $this->value->toJson()];
    }
}
