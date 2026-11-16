<?php

namespace Zouriel\Workflows\Core\Time;

/** A clock that only moves when told to, for test runs and conformance tests. Port of .NET `Time/ManualTimeProvider.cs`. */
final class ManualClock implements Clock
{
    public function __construct(private int $now) {}

    public function now(): int
    {
        return $this->now;
    }

    /** Moves the clock forward by milliseconds. */
    public function advance(int $ms): void
    {
        $this->now += $ms;
    }

    public function set(int $ms): void
    {
        $this->now = $ms;
    }
}
