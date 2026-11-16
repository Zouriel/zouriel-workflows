<?php

namespace Zouriel\Workflows\Core\Registry;

/** The field `type` values (spec §2). Port of .NET `Registry/Descriptors.cs` `FieldTypes`. */
final class FieldTypes
{
    public const string TEXT = 'text';

    public const string LONG_TEXT = 'longText';

    public const string EMAIL = 'email';

    public const string PHONE = 'phone';

    public const string URL = 'url';

    public const string NUMBER = 'number';

    public const string CURRENCY = 'currency';

    public const string BOOLEAN = 'boolean';

    /** `YYYY-MM-DD`. */
    public const string DATE = 'date';

    /** ISO 8601 UTC. */
    public const string DATE_TIME = 'dateTime';

    /** One of the field's options (the stored value). */
    public const string ENUM = 'enum';

    /** A user id. */
    public const string USER = 'user';

    /** One related record of the field's entity (its id). */
    public const string RELATION = 'relation';

    /** Many related records of the field's entity. */
    public const string COLLECTION = 'collection';

    public const string JSON = 'json';

    /** True for types holding a single value (not relations or collections). */
    public static function isScalar(string $type): bool
    {
        return $type !== self::RELATION && $type !== self::COLLECTION;
    }
}
