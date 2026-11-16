<?php

namespace Zouriel\Workflows\Core\Time;

/** The real time, to the millisecond. */
final class SystemClock implements Clock
{
    public function now(): int
    {
        return (int) floor(microtime(true) * 1000);
    }
}
