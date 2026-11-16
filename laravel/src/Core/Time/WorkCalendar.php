<?php

namespace Zouriel\Workflows\Core\Time;

use DateTimeImmutable;
use DateTimeZone;

/**
 * Adds durations to instants in plain or working time (spec §9). Port of .NET `Time/Durations.cs`
 * `WorkCalendar`, step for step: months and years by calendar, then into working time, then whole
 * working days, then working hours; local times inside a DST gap move forward by the minute.
 *
 * Instants are milliseconds since 1970 UTC; "local" times are the wall-clock time in `zone`, held as
 * milliseconds as if that wall clock were UTC.
 */
final class WorkCalendar
{
    private const int DAY = 86400000;

    /** @var array<int, true> working weekdays, 0 = Sunday */
    private array $days;

    /** @var array<string, true> holidays as YYYY-MM-DD */
    private array $holidays;

    /** Opening and closing times, in milliseconds after midnight. */
    private int $start;

    private int $end;

    private bool $utc;

    /**
     * @param  list<int>|null  $days  working weekdays (0 = Sunday), default Monday–Friday
     * @param  int|null  $start  opening, minutes after midnight (default 09:00)
     * @param  int|null  $end  closing, minutes after midnight (default 17:00)
     * @param  list<string>  $holidays  YYYY-MM-DD
     */
    public function __construct(
        public readonly DateTimeZone $zone,
        ?array $days = null,
        ?int $start = null,
        ?int $end = null,
        array $holidays = [],
        public readonly bool $hasWorkingTime = true,
    ) {
        $days = $days === null || $days === [] ? [1, 2, 3, 4, 5] : $days;
        $this->days = array_fill_keys($days, true);
        $this->start = ($start ?? 9 * 60) * 60000;
        $this->end = ($end ?? 17 * 60) * 60000;
        if ($this->end <= $this->start) {
            $this->end = min($this->start + 8 * 3600000, self::DAY - 60000);
        }
        $this->holidays = array_fill_keys($holidays, true);
        $this->utc = in_array($zone->getName(), ['UTC', 'Etc/UTC', 'Z', '+00:00'], true);
    }

    /** Plain UTC time with no working calendar. */
    public static function utc(): self
    {
        return new self(new DateTimeZone('UTC'), hasWorkingTime: false);
    }

    /** An instant as local time. */
    public function toLocal(int $instant): int
    {
        return $instant + $this->offset($instant);
    }

    /** A local time as an instant (times inside a DST gap move forward to the first valid minute). */
    public function fromLocal(int $local): int
    {
        if ($this->utc) {
            return $local;
        }
        for ($guard = 0; $guard < 24 * 60; $guard++) {
            $valid = [];
            foreach (array_unique([$this->offset($local - 14 * 3600000), $this->offset($local), $this->offset($local + 14 * 3600000)]) as $offset) {
                if ($this->offset($local - $offset) === $offset) {
                    $valid[] = $offset;
                }
            }
            if ($valid !== []) {
                // An ambiguous time (clocks going back) reads as standard time, the smaller offset, as .NET does.
                return $local - min($valid);
            }
            $local += 60000;
        }

        return $local - $this->offset($local);
    }

    /** The local date (YYYY-MM-DD) of an instant. */
    public function today(int $now): string
    {
        return gmdate('Y-m-d', intdiv(self::floorDay($this->toLocal($now)), 1000));
    }

    /**
     * Adds a duration; `$working` counts only working time (spec §9). A result beyond the calendar's range
     * (year 9999) is `Instant::MAX` (never), before year 1 `Instant::MIN`; it never throws.
     */
    public function add(int $from, IsoDuration $d, bool $working = false): int
    {
        $result = $working && $this->hasWorkingTime && ! $d->negative ? $this->addWorking($from, $d) : $this->addPlain($from, $d);
        if ($result === null || $result > Instant::MAX) {
            return $d->negative ? Instant::MIN : Instant::MAX;
        }

        return $result < Instant::MIN ? Instant::MIN : $result;
    }

    /**
     * Adds an exact length of time (milliseconds). `$working` counts only working hours; without a working
     * calendar, or when not `$working`, it is plain elapsed time. The inverse of `workingTimeBetween`.
     */
    public function addTime(int $from, int $span, bool $working = false): int
    {
        $span = max(0, $span);
        if (! $working || ! $this->hasWorkingTime) {
            return min(Instant::MAX, $from + $span);
        }
        if ($span === 0) {
            return $from;
        }
        $local = $this->toLocal($from);
        $remaining = $span;
        while ($remaining > 0) {
            $local = $this->moveIntoWorkingTime($local);
            if ($local === null) {
                return Instant::MAX;
            }
            $available = self::floorDay($local) + $this->end - $local;
            if ($remaining <= $available) {
                $local += $remaining;
                $remaining = 0;
            } else {
                $remaining -= $available;
                $local = $this->nextWorkingStart(self::floorDay($local) + self::DAY);
            }
        }

        return $local > Instant::MAX ? Instant::MAX : $this->fromLocal($local);
    }

    /**
     * The time between two instants (milliseconds): working hours only when `$working` and a working calendar
     * exists, else plain elapsed time. Zero when `$to` is not after `$from`. The inverse of `addTime`.
     */
    public function workingTimeBetween(int $from, int $to, bool $working = true): int
    {
        if ($to <= $from) {
            return 0;
        }
        if (! $working || ! $this->hasWorkingTime) {
            return $to - $from;
        }
        $max = IsoDuration::MAX_DAYS * self::DAY;
        if ($to - $from > $max) {
            $to = $from + $max;
        }
        $a = $this->toLocal($from);
        $b = $this->toLocal($to);
        $total = 0;
        for ($day = self::floorDay($a); $day <= self::floorDay($b); $day += self::DAY) {
            if (! $this->isWorkingDay($day)) {
                continue;
            }
            $lo = max($day + $this->start, $a);
            $hi = min($day + $this->end, $b);
            if ($hi > $lo) {
                $total += $hi - $lo;
            }
        }

        return $total;
    }

    /** Whether a local day (any local time in it) is a working day: a working weekday and not a holiday. */
    public function isWorkingDay(int $local): bool
    {
        $days = intdiv(self::floorDay($local), self::DAY);

        return isset($this->days[(($days + 4) % 7 + 7) % 7]) && ! isset($this->holidays[gmdate('Y-m-d', $days * 86400)]);
    }

    /** A date (YYYY-MM-DD) at a local time of day (minutes after midnight), as an instant. */
    public function atLocal(string $date, int $minutes): int
    {
        [$y, $m, $d] = array_map('intval', explode('-', $date));

        return $this->fromLocal(gmmktime(0, 0, 0, $m, $d, $y) * 1000 + $minutes * 60000);
    }

    private function addPlain(int $from, IsoDuration $d): ?int
    {
        $sign = $d->negative ? -1 : 1;
        $local = self::addMonths($this->toLocal($from), $sign * $d->years * 12);
        $local = $local === null ? null : self::addMonths($local, $sign * $d->months);
        if ($local === null) {
            return null;
        }
        $local += $sign * $d->totalDays() * self::DAY;
        if ($local > Instant::MAX || $local < Instant::MIN) {
            return null;
        }

        return $this->fromLocal($local) + $sign * $d->timePartMs();
    }

    private function addWorking(int $from, IsoDuration $d): ?int
    {
        $local = self::addMonths($this->toLocal($from), $d->years * 12);
        $local = $local === null ? null : self::addMonths($local, $d->months);
        $local = $local === null ? null : $this->moveIntoWorkingTime($local);
        if ($local === null) {
            return null;
        }
        for ($i = 0; $i < $d->totalDays(); $i++) {
            $local += self::DAY;
            while (! $this->isWorkingDay($local)) {
                $local += self::DAY;
                if ($local > Instant::MAX) {
                    return null;
                }
            }
        }
        $remaining = $d->timePartMs();
        while ($remaining > 0) {
            $local = $this->moveIntoWorkingTime($local);
            if ($local === null) {
                return null;
            }
            $available = self::floorDay($local) + $this->end - $local;
            if ($remaining <= $available) {
                $local += $remaining;
                $remaining = 0;
            } else {
                $remaining -= $available;
                $local = $this->nextWorkingStart(self::floorDay($local) + self::DAY);
            }
        }

        return $local > Instant::MAX ? null : $this->fromLocal($local);
    }

    /** The same moment if it is inside working hours, else the next start of working hours. */
    public function moveIntoWorkingTime(int $local): ?int
    {
        $day = self::floorDay($local);
        $time = $local - $day;
        if ($this->isWorkingDay($day) && $time >= $this->start && $time < $this->end) {
            return $local;
        }
        if ($this->isWorkingDay($day) && $time < $this->start) {
            return $day + $this->start;
        }
        $next = $this->nextWorkingStart($day + self::DAY);

        return $next > Instant::MAX ? null : $next;
    }

    private function nextWorkingStart(int $date): int
    {
        $day = self::floorDay($date);
        for ($i = 0; $i < 800 && ! $this->isWorkingDay($day); $i++) {
            $day += self::DAY;
        }

        return $day + $this->start;
    }

    /** Calendar months added to a local time, the day kept or moved back to the month's last day (.NET `AddMonths`). */
    private static function addMonths(int $local, int $months): ?int
    {
        if ($months === 0) {
            return $local;
        }
        $day = self::floorDay($local);
        [$y, $m, $d] = array_map('intval', explode('-', gmdate('Y-n-j', intdiv($day, 1000))));
        $total = $y * 12 + ($m - 1) + $months;
        $year = intdiv($total, 12);
        $month = $total % 12 + 1;
        if ($year > 9999 || $year < 1) {
            return null;
        }
        $last = (int) gmdate('t', gmmktime(0, 0, 0, $month, 1, $year));

        return gmmktime(0, 0, 0, $month, min($d, $last), $year) * 1000 + ($local - $day);
    }

    private static function floorDay(int $ms): int
    {
        return intdiv($ms, self::DAY) * self::DAY - ($ms % self::DAY < 0 ? self::DAY : 0);
    }

    /** The zone's offset from UTC at an instant, in milliseconds. */
    private function offset(int $instant): int
    {
        if ($this->utc) {
            return 0;
        }

        return $this->zone->getOffset(new DateTimeImmutable('@'.intdiv($instant, 1000))) * 1000;
    }
}
