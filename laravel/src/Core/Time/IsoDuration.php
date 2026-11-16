<?php

namespace Zouriel\Workflows\Core\Time;

use Zouriel\Forms\Js;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * An ISO 8601 duration (spec §9), kept in its parts so months and days stay calendar-aware. Port of
 * the `IsoDuration` record in .NET `Time/Durations.cs`; `WorkCalendar` adds durations to instants.
 */
final readonly class IsoDuration
{
    /** The longest duration accepted, in days (50 years of 365 days); longer ones are invalid. */
    public const int MAX_DAYS = 50 * 365;

    private const string PATTERN = '/^(-)?P(?!$)(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?=\d)(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$/';

    public function __construct(
        public bool $negative = false,
        public int $years = 0,
        public int $months = 0,
        public int $weeks = 0,
        public int $days = 0,
        public int $hours = 0,
        public int $minutes = 0,
        public int $seconds = 0,
    ) {}

    /** Reads a duration; null for malformed text and for durations longer than 50 years. */
    public static function tryParse(?string $text): ?self
    {
        if ($text === null || Js::trim($text) === '' || ! preg_match(self::PATTERN, Js::trim($text), $m, PREG_UNMATCHED_AS_NULL)) {
            return null;
        }
        $parts = array_fill(0, 9, 0);
        for ($i = 2; $i <= 8; $i++) {
            if (($m[$i] ?? null) === null) {
                continue;
            }
            // Anything that does not fit an int is far beyond the limit anyway.
            if (strlen(ltrim($m[$i], '0')) > 9 || (int) $m[$i] > 50 * 366 * 24 * 60) {
                return null;
            }
            $parts[$i] = (int) $m[$i];
        }
        $days = $parts[2] * 365.0 + $parts[3] * 30.0 + $parts[4] * 7.0 + $parts[5] + $parts[6] / 24.0 + $parts[7] / 1440.0 + $parts[8] / 86400.0;
        if ($days > self::MAX_DAYS) {
            return null;
        }

        return new self($m[1] !== null, $parts[2], $parts[3], $parts[4], $parts[5], $parts[6], $parts[7], $parts[8]);
    }

    /** Reads a duration, refusing an invalid one with the code `badDuration`. */
    public static function parse(string $text): self
    {
        return self::tryParse($text) ?? throw new WorkflowException('badDuration', "'{$text}' is not a duration (ISO 8601, at most 50 years).");
    }

    public function isZero(): bool
    {
        return $this->years === 0 && $this->months === 0 && $this->weeks === 0 && $this->days === 0
            && $this->hours === 0 && $this->minutes === 0 && $this->seconds === 0;
    }

    /** Whole days in the date part (weeks count 7). */
    public function totalDays(): int
    {
        return $this->weeks * 7 + $this->days;
    }

    /** The time part (hours, minutes, seconds) in milliseconds. */
    public function timePartMs(): int
    {
        return (($this->hours * 60 + $this->minutes) * 60 + $this->seconds) * 1000;
    }

    /** An approximate length in milliseconds (years 365 days, months 30), as .NET's `Approximate`; the sign is ignored. */
    public function approximateMs(): int
    {
        return $this->approximateSeconds() * 1000;
    }

    /** An approximate length in seconds, for ordering and limits only (years 365 days, months 30); the sign is ignored. */
    public function approximateSeconds(): int
    {
        return (($this->years * 365 + $this->months * 30 + $this->weeks * 7 + $this->days) * 24 + $this->hours) * 3600
            + $this->minutes * 60 + $this->seconds;
    }
}
