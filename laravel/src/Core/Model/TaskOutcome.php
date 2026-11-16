<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** One button of a `task` step. Port of .NET `TaskOutcome`. */
final class TaskOutcome
{
    public string $id = '';

    /** The outcome key (the task's `outcome` output). */
    public string $key = '';

    public string $label = '';

    public ?string $tone = null;

    /** @var list<Step> */
    public array $steps = [];

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.TaskOutcome');
        $outcome = new self;
        $outcome->id = Read::string($value, 'id', $path);
        $outcome->key = Read::string($value, 'key', $path);
        $outcome->label = Read::string($value, 'label', $path);
        $outcome->tone = Read::optionalString($value, 'tone', $path);
        $outcome->steps = Step::list($value, 'steps', $path) ?? [];

        return $outcome;
    }

    public function toJson(): stdClass
    {
        return Write::object(['id' => $this->id, 'key' => $this->key, 'label' => $this->label, 'tone' => $this->tone, 'steps' => Write::list($this->steps)]);
    }
}
