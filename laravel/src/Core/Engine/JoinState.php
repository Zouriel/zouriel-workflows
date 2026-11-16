<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;

/** A parallel or parallel-forEach step waiting for its child tokens. Port of .NET `Engine/RunState.cs` `JoinState`. */
final class JoinState
{
    /**
     * @param  list<string>  $children
     * @param  list<string>  $done
     * @param  list<stdClass>|null  $items  forEach: all items
     * @param  list<int>|null  $pending  forEach: indexes not yet started
     */
    public function __construct(
        public string $stepId = '',
        public string $tokenId = '',
        public string $kind = 'parallel',
        public array $children = [],
        public array $done = [],
        public int $required = 0,
        public ?array $items = null,
        public ?array $pending = null,
        public int $concurrency = 5,
    ) {}

    public function toJson(): stdClass
    {
        return (object) array_filter(get_object_vars($this), fn ($v) => $v !== null);
    }

    public static function fromJson(stdClass $v): self
    {
        return new self($v->stepId ?? '', $v->tokenId ?? '', $v->kind ?? 'parallel', $v->children ?? [], $v->done ?? [], (int) ($v->required ?? 0),
            $v->items ?? null, isset($v->pending) ? array_map('intval', $v->pending) : null, (int) ($v->concurrency ?? 5));
    }
}
