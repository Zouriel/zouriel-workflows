<?php

namespace Zouriel\Workflows\Core\Time;

use DateTimeImmutable;
use DateTimeZone;

/**
 * Instants as whole milliseconds since 1970-01-01 UTC (the engine's precision, spec §11 audit times),
 * written as `YYYY-MM-DDTHH:mm:ss.fffZ` like .NET `Values.FormatInstant`. `MAX` and `MIN` stand for
 * .NET's `DateTimeOffset.MaxValue` ("never") and `MinValue`.
 */
final class Instant
{
    /** 9999-12-31T23:59:59.999Z: a time that never arrives. */
    public const int MAX = 253402300799999;

    /** 0001-01-01T00:00:00.000Z. */
    public const int MIN = -62135596800000;

    /** ISO 8601 UTC with milliseconds. */
    public static function format(int $ms): string
    {
        $seconds = intdiv($ms, 1000);
        $fraction = $ms % 1000;
        if ($fraction < 0) {
            $seconds--;
            $fraction += 1000;
        }

        return gmdate('Y-m-d\TH:i:s', $seconds).sprintf('.%03dZ', $fraction);
    }

    /**
     * Reads an ISO 8601 date-time (a date alone is midnight UTC); text without an offset is UTC (.NET's
     * `DateTimeStyles.AssumeUniversal`). Null when it is not one.
     */
    public static function parse(?string $text): ?int
    {
        if ($text === null || ! preg_match('/^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?)?\s*(Z|[+-]\d{2}:?\d{2})?$/i', trim($text), $m)) {
            return null;
        }
        [$y, $mo, $d] = [(int) $m[1], (int) $m[2], (int) $m[3]];
        if (! checkdate($mo, $d, $y) || (int) ($m[4] ?? 0) > 23 || (int) ($m[5] ?? 0) > 59 || (int) ($m[6] ?? 0) > 59) {
            return null;
        }
        $ms = gmmktime((int) ($m[4] ?? 0), (int) ($m[5] ?? 0), (int) ($m[6] ?? 0), $mo, $d, $y) * 1000
            + (int) substr(str_pad($m[7] ?? '', 3, '0'), 0, 3);
        $zone = $m[8] ?? '';
        if ($zone !== '' && strtoupper($zone) !== 'Z') {
            $sign = $zone[0] === '-' ? -1 : 1;
            $digits = str_replace(':', '', substr($zone, 1));
            $ms -= $sign * ((int) substr($digits, 0, 2) * 60 + (int) substr($digits, 2, 2)) * 60000;
        }

        return $ms;
    }

    /** The instant as a PHP date in a time zone (UTC by default). */
    public static function toDateTime(int $ms, ?DateTimeZone $zone = null): DateTimeImmutable
    {
        $seconds = intdiv($ms, 1000);
        $fraction = $ms % 1000;
        if ($fraction < 0) {
            $seconds--;
            $fraction += 1000;
        }
        $date = (new DateTimeImmutable('@'.$seconds))->setTime(...self::clock($seconds, $fraction));

        return $zone === null ? $date : $date->setTimezone($zone);
    }

    /** @return array{0: int, 1: int, 2: int, 3: int} */
    private static function clock(int $seconds, int $fraction): array
    {
        $inDay = (($seconds % 86400) + 86400) % 86400;

        return [intdiv($inDay, 3600), intdiv($inDay % 3600, 60), $inDay % 60, $fraction * 1000];
    }
}
