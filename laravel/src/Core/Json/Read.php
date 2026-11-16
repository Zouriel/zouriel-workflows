<?php

namespace Zouriel\Workflows\Core\Json;

use stdClass;

/**
 * Typed reads from decoded JSON objects (`Zouriel\Forms\Json::decode`), with the strictness of
 * .NET's System.Text.Json as the reference uses it (`Model/Converters.cs` `JsonOptions`): property
 * names are case-sensitive, unknown properties are ignored, a value of the wrong JSON type is refused
 * with the path to it. Where .NET would leave a non-nullable member null (and fail later), an absent
 * or null value gives the member's default instead.
 */
final class Read
{
    public static function has(stdClass $object, string $key): bool
    {
        return property_exists($object, $key);
    }

    /** The raw JSON value (null when absent). */
    public static function value(stdClass $object, string $key): mixed
    {
        return $object->{$key} ?? null;
    }

    public static function string(stdClass $object, string $key, string $path, string $default = ''): string
    {
        return self::optionalString($object, $key, $path) ?? $default;
    }

    public static function optionalString(stdClass $object, string $key, string $path): ?string
    {
        $value = $object->{$key} ?? null;

        return $value === null || is_string($value) ? $value : self::fail("{$path}.{$key}", 'System.String');
    }

    public static function int(stdClass $object, string $key, string $path, int $default): int
    {
        if (! property_exists($object, $key)) {
            return $default;
        }

        return self::toInt($object->{$key}, "{$path}.{$key}");
    }

    public static function optionalInt(stdClass $object, string $key, string $path): ?int
    {
        $value = $object->{$key} ?? null;

        return $value === null ? null : self::toInt($value, "{$path}.{$key}", 'System.Nullable`1[System.Int32]');
    }

    public static function bool(stdClass $object, string $key, string $path, bool $default = false): bool
    {
        if (! property_exists($object, $key)) {
            return $default;
        }
        $value = $object->{$key};

        return is_bool($value) ? $value : self::fail("{$path}.{$key}", 'System.Boolean');
    }

    public static function optionalBool(stdClass $object, string $key, string $path): ?bool
    {
        $value = $object->{$key} ?? null;

        return $value === null || is_bool($value) ? $value : self::fail("{$path}.{$key}", 'System.Nullable`1[System.Boolean]');
    }

    /** A JSON object member (`JsonObject` in .NET), null when absent or null. */
    public static function object(stdClass $object, string $key, string $path): ?stdClass
    {
        $value = $object->{$key} ?? null;

        return $value === null || $value instanceof stdClass ? $value : self::fail("{$path}.{$key}", 'System.Text.Json.Nodes.JsonObject');
    }

    /**
     * A JSON array member, each item read by `$item(value, path)`; null when absent or null.
     *
     * @template T
     *
     * @param  callable(mixed, string): T  $item
     * @return list<T>|null
     */
    public static function list(stdClass $object, string $key, string $path, callable $item): ?array
    {
        $value = $object->{$key} ?? null;
        if ($value === null) {
            return null;
        }
        if (! is_array($value)) {
            self::fail("{$path}.{$key}", 'System.Collections.Generic.List');
        }
        $out = [];
        foreach ($value as $index => $entry) {
            $out[] = $item($entry, "{$path}.{$key}[{$index}]");
        }

        return $out;
    }

    /**
     * A JSON object read as a map (`Dictionary<string, T>` in .NET); null when absent or null.
     *
     * @template T
     *
     * @param  callable(mixed, string): T  $item
     * @return array<string, T>|null
     */
    public static function map(stdClass $object, string $key, string $path, callable $item): ?array
    {
        $value = $object->{$key} ?? null;
        if ($value === null) {
            return null;
        }
        if (! $value instanceof stdClass) {
            self::fail("{$path}.{$key}", 'System.Collections.Generic.Dictionary');
        }
        $out = [];
        foreach (get_object_vars($value) as $name => $entry) {
            $out[(string) $name] = $item($entry, "{$path}.{$key}.{$name}");
        }

        return $out;
    }

    /** @return list<string>|null */
    public static function strings(stdClass $object, string $key, string $path): ?array
    {
        return self::list($object, $key, $path, self::itemString(...));
    }

    public static function itemString(mixed $value, string $path): string
    {
        return is_string($value) ? $value : self::fail($path, 'System.String');
    }

    /** An object where .NET deserializes the model class `$type` (e.g. `Zouriel.Workflows.Model.State`). */
    public static function itemObject(mixed $value, string $path, string $type): stdClass
    {
        return $value instanceof stdClass ? $value : self::fail($path, $type);
    }

    /**
     * A member read as a model class by `$read(object, path)`; null when absent or null.
     *
     * @template T
     *
     * @param  callable(stdClass, string): T  $read
     * @return T|null
     */
    public static function model(stdClass $object, string $key, string $path, string $type, callable $read): mixed
    {
        $value = $object->{$key} ?? null;

        return $value === null ? null : $read(self::itemObject($value, "{$path}.{$key}", $type), "{$path}.{$key}");
    }

    public static function toInt(mixed $value, string $path, string $type = 'System.Int32'): int
    {
        if (is_float($value) && is_finite($value) && floor($value) === $value && abs($value) <= 2147483647) {
            return (int) $value;
        }

        return self::fail($path, $type);
    }

    public static function fail(string $path, string $type): never
    {
        throw new JsonShapeException("The JSON value could not be converted to {$type}. Path: {$path}.");
    }
}
