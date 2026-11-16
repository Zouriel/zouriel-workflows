<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Runs its steps once per item of a related collection (output `count`). Port of .NET `ForEachStep`. */
final class ForEachStep extends Step
{
    /** Token path of a collection field (e.g. `entity.documents`). */
    public string $collection = '';

    /** Only items matching this (on `item.*`) are used; null = all. */
    public ?Condition $when = null;

    /** `sequential` (default) or `parallel`. */
    public ?string $mode = null;

    /** Parallel mode: items running at once (default 5). */
    public ?int $concurrency = null;

    /** The most items used (1–500, default 100). */
    public int $max = 100;

    /** @var list<Step> */
    public array $steps = [];

    public function isParallel(): bool
    {
        return $this->mode === 'parallel';
    }

    protected function read(stdClass $value, string $path): void
    {
        $this->collection = Read::string($value, 'collection', $path);
        $this->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");
        $this->mode = Read::optionalString($value, 'mode', $path);
        $this->concurrency = Read::optionalInt($value, 'concurrency', $path);
        $this->max = Read::int($value, 'max', $path, 100);
        $this->steps = Step::list($value, 'steps', $path) ?? [];
    }

    public function children(): array
    {
        return [['each', $this->steps]];
    }

    protected function members(): array
    {
        return [
            'collection' => $this->collection, 'when' => $this->when?->toJson(), 'mode' => $this->mode,
            'concurrency' => $this->concurrency, 'max' => $this->max, 'steps' => Write::list($this->steps),
        ];
    }
}
