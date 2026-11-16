<?php

namespace Zouriel\Workflows\Core\Time;

use InvalidArgumentException;

/**
 * A standard 5-field cron expression (minute hour day-of-month month day-of-week) with `*`, lists,
 * ranges and steps. Day-of-week 0 and 7 are Sunday. Port of .NET `Time/Cron.cs`; finding the next
 * run in a time zone comes with the engine.
 */
final class Cron
{
    /** @var array<int, bool> */
    public private(set) array $minutes = [];

    /** @var array<int, bool> */
    public private(set) array $hours = [];

    /** @var array<int, bool> */
    public private(set) array $daysOfMonth = [];

    /** @var array<int, bool> */
    public private(set) array $months = [];

    /** @var array<int, bool> Sunday is 0. */
    public private(set) array $daysOfWeek = [];

    /** True when the day-of-month field is `*` or `?`. */
    public private(set) bool $anyDayOfMonth;

    /** True when the day-of-week field is `*` or `?`. */
    public private(set) bool $anyDayOfWeek;

    /** @param  list<string>  $fields */
    private function __construct(array $fields)
    {
        $this->minutes = self::fill($fields[0], 0, 59);
        $this->hours = self::fill($fields[1], 0, 23);
        $this->anyDayOfMonth = in_array($fields[2], ['*', '?'], true);
        $this->daysOfMonth = self::fill($fields[2], 1, 31);
        $this->months = self::fill($fields[3], 1, 12);
        $this->anyDayOfWeek = in_array($fields[4], ['*', '?'], true);
        $days = self::fill($fields[4], 0, 7);
        if ($days[7] ?? false) {
            $days[0] = true;
        }
        unset($days[7]);
        $this->daysOfWeek = $days;
    }

    /**
     * Reads a 5-field cron expression.
     *
     * @return array{0: ?self, 1: ?string} the schedule, or null and what is wrong with it
     */
    public static function tryParse(?string $expression): array
    {
        $fields = array_values(array_filter(explode(' ', $expression ?? ''), fn ($f) => $f !== ''));
        if (count($fields) !== 5) {
            return [null, 'A schedule needs 5 parts: minute hour day month weekday.'];
        }
        try {
            return [new self($fields), null];
        } catch (InvalidArgumentException $e) {
            return [null, $e->getMessage()];
        }
    }

    /** @return array<int, bool> */
    private static function fill(string $field, int $min, int $max): array
    {
        $target = [];
        foreach (explode(',', $field) as $part) {
            $step = 1;
            $range = $part;
            $slash = strpos($part, '/');
            if ($slash !== false) {
                $step = self::number(substr($part, $slash + 1), 1, $max);
                $range = substr($part, 0, $slash);
            }
            if ($range === '*' || $range === '?') {
                [$low, $high] = [$min, $max];
            } elseif (str_contains($range, '-')) {
                $bits = explode('-', $range);
                $low = self::number($bits[0], $min, $max);
                $high = self::number($bits[1], $min, $max);
                if ($high < $low) {
                    throw new InvalidArgumentException("'{$part}' is a backwards range.");
                }
            } else {
                $low = self::number($range, $min, $max);
                $high = $slash !== false ? $max : $low;
            }
            for ($i = $low; $i <= $high; $i += $step) {
                $target[$i] = true;
            }
        }

        return $target;
    }

    private static function number(string $text, int $min, int $max): int
    {
        if (! preg_match('/^[0-9]+$/', $text) || strlen(ltrim($text, '0')) > 10 || (int) $text < $min || (int) $text > $max) {
            throw new InvalidArgumentException("'{$text}' must be a number from {$min} to {$max}.");
        }

        return (int) $text;
    }
}
