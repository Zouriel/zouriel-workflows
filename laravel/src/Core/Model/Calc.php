<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A calculation from the form engine with token paths in its arguments. Port of .NET `Model/Definition.cs` `Calc`. */
final class Calc
{
    public string $op = 'sum';

    /** @var list<CalcArg> */
    public array $args = [];

    public ?int $decimals = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $calc = new self;
        $calc->op = Read::string($value, 'op', $path, 'sum');
        $calc->args = Read::list($value, 'args', $path, CalcArg::fromJson(...)) ?? [];
        $calc->decimals = Read::optionalInt($value, 'decimals', $path);

        return $calc;
    }

    public function toJson(): stdClass
    {
        return Write::object(['op' => $this->op, 'args' => Write::list($this->args), 'decimals' => $this->decimals]);
    }

    /** @return list<string> every token path the calculation reads, nested calculations included (.NET `Conditions.CalcRefs`) */
    public function refs(): array
    {
        return array_merge([], ...array_map(fn (CalcArg $a) => $a->ref !== null ? [$a->ref] : ($a->calc?->refs() ?? []), $this->args));
    }
}
