<?php

namespace Zouriel\Workflows\Core\Engine;

use Closure;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\Views\SimulationEntry;
use Zouriel\Workflows\Core\Time\Instant;

/** Collects what a test run did, side effects that were skipped included. Port of .NET `Engine/SimulationLog.cs`. */
final class SimulationLog
{
    /** @var list<SimulationEntry> */
    public array $entries = [];

    /** @param  Closure(): int  $now */
    public function __construct(private readonly Closure $now) {}

    public function add(string $kind, string $action, ?string $subject, ?string $text, mixed $data = null): void
    {
        $this->entries[] = new SimulationEntry($kind, $action, $subject, $text, Json::normalize($data), Instant::format(($this->now)()));
    }
}
