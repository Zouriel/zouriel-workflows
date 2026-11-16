<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;

/** One comparison: the value at `ref` against `value` with `op`. Port of .NET `ConditionRule`. */
final class ConditionRule extends Condition
{
    public string $ref = '';

    public string $op = 'eq';

    public ?Operand $value = null;

    public ?Operand $value2 = null;

    public function toJson(): stdClass
    {
        $out = (object) ['ref' => $this->ref, 'op' => $this->op];
        if ($this->value !== null) {
            $out->value = $this->value->toJson();
        }
        if ($this->value2 !== null) {
            $out->value2 = $this->value2->toJson();
        }

        return $out;
    }

    public function isComplete(): bool
    {
        if ($this->ref === '') {
            return false;
        }
        $arity = self::arity($this->op);

        return ! ($arity >= 1 && ! $this->value?->isFilled()) && ! ($arity === 2 && ! $this->value2?->isFilled());
    }

    public function refs(): array
    {
        return array_merge($this->ref !== '' ? [$this->ref] : [], $this->value?->refs() ?? [], $this->value2?->refs() ?? []);
    }
}
