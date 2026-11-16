<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Sets workflow variables. Port of .NET `SetVariablesStep`. */
final class SetVariablesStep extends Step
{
    /** @var list<VariableValue> */
    public array $values = [];

    protected function read(stdClass $value, string $path): void
    {
        $this->values = Read::list($value, 'values', $path, VariableValue::fromJson(...)) ?? [];
    }

    protected function members(): array
    {
        return ['values' => Write::list($this->values)];
    }
}
