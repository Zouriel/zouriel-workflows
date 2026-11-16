<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;

/** A position in a step list: the list id, the step index, its retry attempt and, inside `forEach`, the current item. Port of .NET `Engine/RunState.cs` `Frame`. */
final class Frame
{
    public function __construct(
        public string $list = 'root',
        public int $index = 0,
        public int $attempt = 1,
        public ?stdClass $item = null,
        public ?int $itemIndex = null,
    ) {}

    public function toJson(): stdClass
    {
        return (object) array_filter(['list' => $this->list, 'index' => $this->index, 'attempt' => $this->attempt, 'item' => $this->item, 'itemIndex' => $this->itemIndex],
            fn ($v) => $v !== null);
    }

    public static function fromJson(stdClass $v): self
    {
        return new self($v->list ?? 'root', (int) ($v->index ?? 0), (int) ($v->attempt ?? 1), $v->item ?? null, isset($v->itemIndex) ? (int) $v->itemIndex : null);
    }
}
