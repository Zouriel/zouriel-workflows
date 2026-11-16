<?php

namespace Zouriel\Workflows\Core\Json;

use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;

/**
 * Workflow values as text (port of .NET `Json/Values.cs` `Text`). Values are the decoded JSON values
 * `zouriel/forms` works with: null, string, float, bool, list and stdClass.
 */
final class Values
{
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
}
