<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** Pauses the run (outputs `timedOut` and, for signals, `data`). Port of .NET `WaitStep`. */
final class WaitStep extends Step
{
    public WaitUntil $until;

    public function __construct()
    {
        $this->until = new WaitUntil;
    }

    protected function read(stdClass $value, string $path): void
    {
        $this->until = Read::model($value, 'until', $path, 'Zouriel.Workflows.Model.WaitUntil', WaitUntil::fromJson(...)) ?? new WaitUntil;
    }

    public function children(): array
    {
        return $this->until->onTimeout === null ? [] : [['onTimeout', $this->until->onTimeout->steps]];
    }

    protected function members(): array
    {
        return ['until' => $this->until->toJson()];
    }
}
