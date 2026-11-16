<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Asks a person to choose one of up to eight outcomes; the first decision wins. Port of .NET `TaskStep`. */
final class TaskStep extends HumanStep
{
    /** @var list<TaskOutcome> */
    public array $outcomes = [];

    /** Run when the due time passes first. */
    public StepList $onTimeout;

    public function __construct()
    {
        $this->onTimeout = new StepList;
    }

    protected function read(stdClass $value, string $path): void
    {
        parent::read($value, $path);
        $this->outcomes = Read::list($value, 'outcomes', $path, TaskOutcome::fromJson(...)) ?? [];
        $this->onTimeout = StepList::fromJson(Read::value($value, 'onTimeout'), "{$path}.onTimeout") ?? new StepList;
    }

    public function children(): array
    {
        return [...array_map(fn (TaskOutcome $o) => ["outcome:{$o->key}", $o->steps], $this->outcomes), ['onTimeout', $this->onTimeout->steps]];
    }

    protected function members(): array
    {
        return ['outcomes' => Write::list($this->outcomes), 'onTimeout' => $this->onTimeout->toJson(), ...parent::members()];
    }
}
