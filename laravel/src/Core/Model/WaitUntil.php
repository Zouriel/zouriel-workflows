<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** What a `wait` step waits for: a `duration`, a `date`, a `condition` or a `signal`. Port of .NET `WaitUntil`. */
final class WaitUntil
{
    public string $type = 'duration';

    public ?string $duration = null;

    public ?string $calendar = null;

    public ?Operand $date = null;

    /** Signed ISO duration added to the date. */
    public ?string $offset = null;

    /** Time of day `HH:MM` for a date value (default 09:00). */
    public ?string $at = null;

    public ?Condition $when = null;

    public ?string $timeout = null;

    /** Run when the wait times out; null = continue after the step. */
    public ?StepList $onTimeout = null;

    public ?string $signal = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $until = new self;
        $until->type = Read::string($value, 'type', $path, 'duration');
        $until->duration = Read::optionalString($value, 'duration', $path);
        $until->calendar = Read::optionalString($value, 'calendar', $path);
        $until->date = ($date = Read::value($value, 'date')) === null ? null : Operand::fromJson($date, "{$path}.date");
        $until->offset = Read::optionalString($value, 'offset', $path);
        $until->at = Read::optionalString($value, 'at', $path);
        $until->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");
        $until->timeout = Read::optionalString($value, 'timeout', $path);
        $until->onTimeout = StepList::fromJson(Read::value($value, 'onTimeout'), "{$path}.onTimeout");
        $until->signal = Read::optionalString($value, 'signal', $path);

        return $until;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'type' => $this->type, 'duration' => $this->duration, 'calendar' => $this->calendar, 'date' => $this->date?->toJson(),
            'offset' => $this->offset, 'at' => $this->at, 'when' => $this->when?->toJson(), 'timeout' => $this->timeout,
            'onTimeout' => $this->onTimeout?->toJson(), 'signal' => $this->signal,
        ]);
    }
}
