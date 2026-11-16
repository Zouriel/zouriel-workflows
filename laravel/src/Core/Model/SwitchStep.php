<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Runs the case whose values contain the value at `on` (output `case`). Port of .NET `SwitchStep`. */
final class SwitchStep extends Step
{
    /** The token path whose value picks the case. */
    public string $on = '';

    /** @var list<SwitchCase> */
    public array $cases = [];

    public ?StepList $otherwise = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->on = Read::string($value, 'on', $path);
        $this->cases = Read::list($value, 'cases', $path, SwitchCase::fromJson(...)) ?? [];
        $this->otherwise = StepList::fromJson(Read::value($value, 'otherwise'), "{$path}.otherwise");
    }

    public function children(): array
    {
        $out = array_map(fn (SwitchCase $c) => ["case:{$c->id}", $c->steps], $this->cases);
        if ($this->otherwise !== null) {
            $out[] = ['otherwise', $this->otherwise->steps];
        }

        return $out;
    }

    protected function members(): array
    {
        return ['on' => $this->on, 'cases' => Write::list($this->cases), 'otherwise' => $this->otherwise?->toJson()];
    }
}
