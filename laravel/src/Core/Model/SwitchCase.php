<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** One case of a `switch` step, selected by its values (matched like `anyOf`). Port of .NET `SwitchCase`. */
final class SwitchCase
{
    public string $id = '';

    public string $label = '';

    /** @var list<mixed> */
    public array $values = [];

    /** @var list<Step> */
    public array $steps = [];

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.SwitchCase');
        $case = new self;
        $case->id = Read::string($value, 'id', $path);
        $case->label = Read::string($value, 'label', $path);
        $case->values = Read::list($value, 'values', $path, fn ($v) => $v) ?? [];
        $case->steps = Step::list($value, 'steps', $path) ?? [];

        return $case;
    }

    public function toJson(): stdClass
    {
        return Write::object(['id' => $this->id, 'label' => $this->label, 'values' => $this->values, 'steps' => Write::list($this->steps)]);
    }
}
