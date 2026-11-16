<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Runs the first path whose condition holds, else `otherwise` (output `path`). Port of .NET `BranchStep`. */
final class BranchStep extends Step
{
    /** @var list<BranchPath> */
    public array $paths = [];

    public ?StepList $otherwise = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->paths = Read::list($value, 'paths', $path, BranchPath::fromJson(...)) ?? [];
        $this->otherwise = StepList::fromJson(Read::value($value, 'otherwise'), "{$path}.otherwise");
    }

    public function children(): array
    {
        $out = array_map(fn (BranchPath $p) => ["path:{$p->id}", $p->steps], $this->paths);
        if ($this->otherwise !== null) {
            $out[] = ['otherwise', $this->otherwise->steps];
        }

        return $out;
    }

    protected function members(): array
    {
        return ['paths' => Write::list($this->paths), 'otherwise' => $this->otherwise?->toJson()];
    }
}
