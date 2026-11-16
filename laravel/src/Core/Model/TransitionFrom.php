<?php

namespace Zouriel\Workflows\Core\Model;

use Zouriel\Workflows\Core\Json\Read;

/** `"*"` (any non-terminal state) or a list of state ids. Port of .NET `TransitionFrom` and `TransitionFromConverter`. */
final class TransitionFrom
{
    /** @param  list<string>  $states */
    public function __construct(public bool $any = false, public array $states = []) {}

    public static function fromJson(mixed $value, string $path): self
    {
        return match (true) {
            $value === null => new self,
            $value === '*' => new self(true),
            is_string($value) => new self(false, [$value]),
            is_array($value) => new self(false, array_map(fn ($v, $i) => Read::itemString($v, "{$path}[{$i}]"), $value, array_keys($value))),
            default => Read::fail($path, 'System.Collections.Generic.List`1[System.String]'),
        };
    }

    public function toJson(): mixed
    {
        return $this->any ? '*' : $this->states;
    }

    /** Whether the transition can start from `$stateId` (for `*`: any non-terminal state other than the target). */
    public function allows(Lifecycle $lifecycle, string $stateId, string $toId): bool
    {
        if (! $this->any) {
            return in_array($stateId, $this->states, true);
        }
        $state = array_find($lifecycle->states, fn (State $s) => $s->id === $stateId);

        return $state !== null && ! $state->isTerminal() && $stateId !== $toId;
    }
}
