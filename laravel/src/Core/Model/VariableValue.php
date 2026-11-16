<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** One variable a `setVariables` step sets, by variable id. Port of .NET `VariableValue`. */
final class VariableValue
{
    public string $variable = '';

    public Operand $value;

    public function __construct()
    {
        $this->value = Operand::of(null);
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.VariableValue');
        $item = new self;
        $item->variable = Read::string($value, 'variable', $path);
        if (($value->value ?? null) !== null) {
            $item->value = Operand::fromJson($value->value, "{$path}.value");
        }

        return $item;
    }

    public function toJson(): stdClass
    {
        return (object) ['variable' => $this->variable, 'value' => $this->value->toJson()];
    }
}
