<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Starts another automation (outputs `instanceId`, `status`, `outputs`). Port of .NET `SubWorkflowStep`. */
final class SubWorkflowStep extends Step
{
    /** The automation's key. */
    public string $workflow = '';

    /** `self`/null for the same record, or a token path to a relation. */
    public ?string $target = null;

    /** Whether the step waits for the child to finish. */
    public bool $wait = false;

    /** @var array<string, Operand>|null values the child reads as `input.*` */
    public ?array $input = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->workflow = Read::string($value, 'workflow', $path);
        $this->target = Read::optionalString($value, 'target', $path);
        $this->wait = Read::bool($value, 'wait', $path);
        $this->input = Read::map($value, 'input', $path, Operand::fromJson(...));
    }

    protected function members(): array
    {
        return ['workflow' => $this->workflow, 'target' => $this->target, 'wait' => $this->wait, 'input' => Write::map($this->input)];
    }
}
