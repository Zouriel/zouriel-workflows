<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Runs 2–10 paths at once and continues when the join is met (output `completed`). Port of .NET `ParallelStep`. */
final class ParallelStep extends Step
{
    /** @var list<StepList> */
    public array $paths = [];

    public ParallelJoin $join;

    public function __construct()
    {
        $this->join = new ParallelJoin;
    }

    protected function read(stdClass $value, string $path): void
    {
        $this->paths = Read::list($value, 'paths', $path, StepList::fromJson(...)) ?? [];
        $this->join = Read::model($value, 'join', $path, 'Zouriel.Workflows.Model.ParallelJoin', ParallelJoin::fromJson(...)) ?? new ParallelJoin;
    }

    public function children(): array
    {
        return array_map(fn (StepList $p) => ["path:{$p->id}", $p->steps], $this->paths);
    }

    protected function members(): array
    {
        return ['paths' => Write::list($this->paths), 'join' => $this->join->toJson()];
    }
}
