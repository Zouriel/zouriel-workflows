<?php

namespace Zouriel\Workflows\Core\Time;

/** Where the engine reads the time (.NET `TimeProvider`): instants in milliseconds since 1970 UTC. */
interface Clock
{
    public function now(): int;
}
