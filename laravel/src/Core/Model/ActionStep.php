<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Calls an action the app registered (outputs: the action's declared outputs). Port of .NET `ActionStep`. */
final class ActionStep extends Step
{
    /** The registry action key. */
    public string $action = '';

    /** @var array<string, Operand>|null input values by key */
    public ?array $input = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->action = Read::string($value, 'action', $path);
        $this->input = Read::map($value, 'input', $path, Operand::fromJson(...));
    }

    protected function members(): array
    {
        return ['action' => $this->action, 'input' => Write::map($this->input)];
    }
}
