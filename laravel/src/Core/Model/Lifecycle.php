<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A lifecycle: the enum field it owns, its states and its transitions (spec §6). Port of .NET `Lifecycle`. */
final class Lifecycle
{
    /** The `lifecycle: true` enum field the lifecycle owns. */
    public string $field = '';

    /** `enforce` (default) or `observe`. */
    public ?string $mode = null;

    /** @var list<State> */
    public array $states = [];

    /** @var list<Transition> */
    public array $transitions = [];

    /** @var array<string, list<float>>|null editor positions of states (state id → [x, y]); ignored by engines */
    public ?array $layout = null;

    public ?LifecycleReset $reset = null;

    /** Which records the lifecycle is for (spec §6 applies to); absent = every record. */
    public ?Condition $appliesWhen = null;

    /** True unless `mode` is `observe`. */
    public function enforce(): bool
    {
        return $this->mode !== 'observe';
    }

    public static function fromJson(stdClass $value, string $path): self
    {
        $lifecycle = new self;
        $lifecycle->field = Read::string($value, 'field', $path);
        $lifecycle->mode = Read::optionalString($value, 'mode', $path);
        $lifecycle->states = Read::list($value, 'states', $path, State::fromJson(...)) ?? [];
        $lifecycle->transitions = Read::list($value, 'transitions', $path, Transition::fromJson(...)) ?? [];
        $lifecycle->layout = Read::map($value, 'layout', $path, fn ($point, $at) => is_array($point)
            ? array_map(fn ($n) => is_float($n) ? $n : Read::fail($at, 'System.Double'), $point)
            : Read::fail($at, 'System.Double[]'));
        $lifecycle->reset = Read::model($value, 'reset', $path, 'Zouriel.Workflows.Model.LifecycleReset', LifecycleReset::fromJson(...));
        $lifecycle->appliesWhen = Condition::fromJson(Read::value($value, 'appliesWhen'), "{$path}.appliesWhen");

        return $lifecycle;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'field' => $this->field, 'mode' => $this->mode, 'states' => Write::list($this->states), 'transitions' => Write::list($this->transitions),
            'layout' => $this->layout === null ? null : (object) $this->layout, 'reset' => $this->reset?->toJson(),
            'appliesWhen' => $this->appliesWhen?->toJson(),
        ]);
    }
}
