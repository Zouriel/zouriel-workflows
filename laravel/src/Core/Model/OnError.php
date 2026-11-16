<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\JsonShapeException;

/** `"stop"`, `"continue"` or `{ steps }`. Port of .NET `OnError` and `OnErrorConverter`. */
final class OnError
{
    /**
     * @param  string  $mode  `stop` (incident), `continue` (output `error` holds the message) or `steps`
     * @param  list<Step>|null  $steps  for `steps`: run before continuing after the failed step
     */
    public function __construct(public string $mode = 'stop', public ?array $steps = null) {}

    public static function fromJson(mixed $value, string $path): ?self
    {
        return match (true) {
            $value === null => null,
            is_string($value) => new self($value),
            $value instanceof stdClass => new self('steps', Step::list($value, 'steps', $path) ?? []),
            default => throw new JsonShapeException("onError must be a string or an object. Path: {$path}."),
        };
    }

    public function toJson(): mixed
    {
        return $this->mode !== 'steps' ? $this->mode : (object) ['steps' => array_map(fn (Step $s) => $s->toJson(), $this->steps ?? [])];
    }
}
