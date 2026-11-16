<?php

namespace Zouriel\Workflows\Core\Json;

use stdClass;

/**
 * Writing model objects back to JSON values the way .NET's `Definitions.JsonOptions` does: members
 * in declaration order, null members left out (`WhenWritingNull`), maps as objects.
 */
final class Write
{
    /** An object of the non-null members. */
    public static function object(array $members): stdClass
    {
        return (object) array_filter($members, fn ($value) => $value !== null);
    }

    /** An object of every member, nulls included (ASP.NET's web JSON defaults, used by API responses). */
    public static function objectWithNulls(array $members): stdClass
    {
        return (object) $members;
    }

    /**
     * @param  list<object>|null  $items  objects with a `toJson()` method
     * @return list<mixed>|null
     */
    public static function list(?array $items): ?array
    {
        return $items === null ? null : array_map(fn ($item) => $item->toJson(), $items);
    }

    /** @param  array<string, object>|null  $items  objects with a `toJson()` method */
    public static function map(?array $items): ?stdClass
    {
        if ($items === null) {
            return null;
        }
        $out = new stdClass;
        foreach ($items as $key => $item) {
            $out->{$key} = $item->toJson();
        }

        return $out;
    }
}
