<?php

namespace Zouriel\Workflows\Core\Time;

use InvalidArgumentException;

/**
 * A standard 5-field cron expression (minute hour day-of-month month day-of-week) with `*`, lists,
 * ranges and steps. Day-of-week 0 and 7 are Sunday. Port of .NET `Time/Cron.cs`.
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

    /** The first matching minute strictly after `$after`, in `$zone`'s local time (null when none within 5 years). */
    public function next(int $after, WorkCalendar $calendar): ?int
    {
        $minute = 60000;
        $local = intdiv($calendar->toLocal($after), $minute) * $minute + $minute;
        [$y, $m, $d] = array_map('intval', explode('-', gmdate('Y-n-j', intdiv($local, 1000))));
        $limit = gmmktime(0, 0, 0, $m, min($d, (int) gmdate('t', gmmktime(0, 0, 0, $m, 1, $y + 5))), $y + 5) * 1000 + ($local % 86400000);
        while ($local < $limit) {
            $seconds = intdiv($local, 1000);
            [$year, $month, $day, $hour, $min, $weekday] = array_map('intval', explode(' ', gmdate('Y n j G i w', $seconds)));
            if (! ($this->months[$month] ?? false)) {
                $local = gmmktime(0, 0, 0, $month + 1, 1, $year) * 1000;

                continue;
            }
            if (! $this->dayMatches($day, $weekday)) {
                $local = gmmktime(0, 0, 0, $month, $day + 1, $year) * 1000;

                continue;
            }
            if (! ($this->hours[$hour] ?? false)) {
                $local = gmmktime($hour + 1, 0, 0, $month, $day, $year) * 1000;

                continue;
            }
            if (! ($this->minutes[$min] ?? false)) {
                $local += $minute;

                continue;
            }
            $instant = $calendar->fromLocal($local);
            if ($calendar->toLocal($instant) !== $local) {
                // Inside a DST gap: that local minute does not exist.
                $local += $minute;

                continue;
            }

            return $instant;
        }

        return null;
    }

    private function dayMatches(int $day, int $weekday): bool
    {
        $dom = $this->daysOfMonth[$day] ?? false;
        $dow = $this->daysOfWeek[$weekday] ?? false;

        return match (true) {
            $this->anyDayOfMonth && $this->anyDayOfWeek => true,
            $this->anyDayOfMonth => $dow,
            $this->anyDayOfWeek => $dom,
            default => $dom || $dow,
        };
    }
}
