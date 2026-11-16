<?php

namespace Zouriel\Workflows\Core\Engine;

/**
 * A kept SLA clock (spec §6 `sla.pauseOnStatus`, `pauseOnEvent`): what a record instance remembers about a
 * state's time limit while its timer is not planned. Port of .NET `Engine/SlaClocks.cs` `SlaClock`.
 */
final class SlaClock
{
    public const string STATUS = 'status';

    public const string EVENT = 'event';

    /**
     * @param  int  $leftMs  time left when paused (working time when `working`)
     * @param  list<string>  $reasons  why it is paused: `status` and/or `event`
     * @param  bool  $ran  the SLA already ran in this stay: nothing is planned again
     * @param  int|null  $due  a clock running when its lifecycle was archived: when it was due
     */
    public function __construct(public int $leftMs = 0, public bool $working = false, public array $reasons = [], public bool $ran = false, public ?int $due = null) {}

    public function addReason(string $reason): void
    {
        if (! in_array($reason, $this->reasons, true)) {
            $this->reasons[] = $reason;
        }
        usort($this->reasons, fn ($a, $b) => ($a === self::STATUS ? 0 : 1) <=> ($b === self::STATUS ? 0 : 1));
    }

    public function removeReason(string $reason): void
    {
        $this->reasons = array_values(array_filter($this->reasons, fn ($r) => $r !== $reason));
    }

    public function hasReason(string $reason): bool
    {
        return in_array($reason, $this->reasons, true);
    }
}
