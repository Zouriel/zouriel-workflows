<?php

namespace Zouriel\Workflows\Core\Model;

/** Remarks people give with a move or a reset (spec §6): whether they are asked, and the length limit. Port of .NET `Remarks`. */
final class Remarks
{
    public const string NONE = 'none';

    public const string OPTIONAL = 'optional';

    public const string REQUIRED = 'required';

    /** The longest remarks accepted, in characters. */
    public const int MAX_LENGTH = 2000;

    /** A known setting, else `$fallback`. */
    public static function resolve(?string $value, string $fallback): string
    {
        return in_array($value, [self::NONE, self::OPTIONAL, self::REQUIRED], true) ? $value : $fallback;
    }
}
