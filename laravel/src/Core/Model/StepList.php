<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A named list of steps (a parallel path, an `otherwise`, an `onTimeout`). Port of .NET `StepList`. */
final class StepList
{
    public string $id = '';

    public ?string $label = null;

    /** @var list<Step> */
    public array $steps = [];

    public static function fromJson(mixed $value, string $path): ?self
    {
        if ($value === null) {
            return null;
        }
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.StepList');
        $list = new self;
        $list->id = Read::string($value, 'id', $path);
        $list->label = Read::optionalString($value, 'label', $path);
        $list->steps = Step::list($value, 'steps', $path) ?? [];

        return $list;
    }

    public function toJson(): stdClass
    {
        return Write::object(['id' => $this->id, 'label' => $this->label, 'steps' => Write::list($this->steps)]);
    }
}
