<?php

namespace Zouriel\Workflows\Core\Json;

use DateTimeZone;
use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;
use Zouriel\Forms\Values as FormValues;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * Workflow values (port of .NET `Json/Values.cs`). Values are the decoded JSON values `zouriel/forms`
 * works with: null, string, float, bool, list and stdClass.
 */
final class Values
{
    /** Any PHP value (ints, associative arrays…) as a workflow value. */
    public static function normalize(mixed $value): mixed
    {
        return Json::normalize($value);
    }

    /** A value as a template shows it: booleans as Yes/No, numbers as JavaScript prints them, lists joined with ", ". */
    public static function text(mixed $value): string
    {
        $value = Json::normalize($value);

        return match (true) {
            $value === null => '',
            is_string($value) => $value,
            is_bool($value) => $value ? 'Yes' : 'No',
            is_float($value) => Js::numberText($value),
            is_array($value) => implode(', ', array_map(self::text(...), $value)),
            $value instanceof stdClass && property_exists($value, 'label') => self::text($value->label),
            default => Json::encode($value),
        };
    }

    /** A value as JSON text (`null` for null). */
    public static function serialize(mixed $value): string
    {
        return Json::encode(Json::normalize($value));
    }

    /** JSON text as a workflow value. */
    public static function fromJson(?string $json): mixed
    {
        return $json === null ? null : Json::decode($json);
    }

    /** Null, blank text, an empty list or NaN (an empty object is not empty). */
    public static function isEmpty(mixed $value): bool
    {
        return FormValues::isEmpty(Json::normalize($value));
    }

    /** Structural equality: numbers by value, object key order ignored. */
    public static function deepEquals(mixed $a, mixed $b): bool
    {
        return FormValues::deepEquals(Json::normalize($a), Json::normalize($b));
    }

    /** Reads a dot path inside a value (objects by key, lists by index). */
    public static function readPath(mixed $root, array $parts): mixed
    {
        $current = $root;
        foreach ($parts as $part) {
            $current = match (true) {
                $current instanceof stdClass => $current->{$part} ?? null,
                is_array($current) && preg_match('/^[0-9]+$/', $part) === 1 && (int) $part < count($current) => $current[(int) $part],
                default => null,
            };
            if ($current === null) {
                return null;
            }
        }

        return $current;
    }

    /** A copy of an object value with one member set (objects are references in PHP). */
    public static function with(?stdClass $object, string $key, mixed $value): stdClass
    {
        $copy = $object === null ? new stdClass : Json::copy($object);
        $copy->{$key} = $value;

        return $copy;
    }

    /** An object value's members as a PHP map. @return array<string, mixed> */
    public static function members(mixed $object): array
    {
        if (! $object instanceof stdClass) {
            return [];
        }
        $out = [];
        foreach (get_object_vars($object) as $key => $value) {
            $out[(string) $key] = $value;
        }

        return $out;
    }

    /** A PHP map as an object value (keys kept as text, even numeric ones). @param  array<array-key, mixed>  $members */
    public static function object(array $members): stdClass
    {
        $out = new stdClass;
        foreach ($members as $key => $value) {
            $out->{(string) $key} = Json::normalize($value);
        }

        return $out;
    }

    /**
     * An instant for people to read in message text, e.g. `2 Nov 2026, 09:00 UTC`, in `$zone` (UTC when
     * null) and named after it. Machine fields stay ISO 8601 (.NET `Values.FormatForPeople`).
     */
    public static function formatForPeople(int $instant, ?DateTimeZone $zone = null): string
    {
        $zone ??= new DateTimeZone('UTC');
        $isUtc = in_array($zone->getName(), ['UTC', 'Etc/UTC', 'Etc/Universal', 'Z', '+00:00'], true);

        return Instant::toDateTime($instant, $zone)->format('j M Y, H:i').' '.($isUtc ? 'UTC' : $zone->getName());
    }

    /** `YYYY-MM-DD` read as a day for people ("20 Oct 2026"), or null when it is not one. */
    public static function formatDay(string $text): ?string
    {
        if (preg_match('/^(\d{4})-(\d{2})-(\d{2})$/', $text, $m) !== 1 || ! checkdate((int) $m[2], (int) $m[3], (int) $m[1])) {
            return null;
        }

        return gmdate('j M Y', gmmktime(0, 0, 0, (int) $m[2], (int) $m[3], (int) $m[1]));
    }
}
