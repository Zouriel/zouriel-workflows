<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** One path of a `branch` step: taken when its condition holds. Port of .NET `BranchPath`. */
final class BranchPath
{
    public string $id = '';

    public string $label = '';

    public ?Condition $when = null;

    /** @var list<Step> */
    public array $steps = [];

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.BranchPath');
        $branchPath = new self;
        $branchPath->id = Read::string($value, 'id', $path);
        $branchPath->label = Read::string($value, 'label', $path);
        $branchPath->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");
        $branchPath->steps = Step::list($value, 'steps', $path) ?? [];

        return $branchPath;
    }

    public function toJson(): stdClass
    {
        return Write::object(['id' => $this->id, 'label' => $this->label, 'when' => $this->when?->toJson(), 'steps' => Write::list($this->steps)]);
    }
}
