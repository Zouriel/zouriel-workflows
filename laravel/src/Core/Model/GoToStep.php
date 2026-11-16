<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** Jumps back to an earlier step on the current path, at most `maxTimes` times (output `count`). Port of .NET `GoToStep`. */
final class GoToStep extends Step
{
    /** The id of the step to go back to. */
    public string $target = '';

    /** How many jumps (1–50). */
    public int $maxTimes = 1;

    protected function read(stdClass $value, string $path): void
    {
        $this->target = Read::string($value, 'target', $path);
        $this->maxTimes = Read::int($value, 'maxTimes', $path, 1);
    }

    protected function members(): array
    {
        return ['target' => $this->target, 'maxTimes' => $this->maxTimes];
    }
}
