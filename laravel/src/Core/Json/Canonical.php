<?php

namespace Zouriel\Workflows\Core\Json;

use InvalidArgumentException;
use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;

/**
 * JSON Canonicalization Scheme (RFC 8785), used for audit hashes. Byte-identical to .NET's
 * `Values.Canonical`: numbers as JavaScript prints them (integers and floats alike, `1` and `1.0`
 * hash the same), keys ordered by UTF-16 code units, only `"`, `\` and control characters
 * escaped, everything else written as UTF-8.
 */
final class Canonical
{
    public static function encode(mixed $value): string
    {
        return self::write(Json::normalize($value));
    }

    private static function write(mixed $value): string
    {
        return match (true) {
            $value === null => 'null',
            is_bool($value) => $value ? 'true' : 'false',
            is_float($value) => is_finite($value) ? Js::numberText($value) : throw new InvalidArgumentException('JCS cannot encode NaN or Infinity.'),
            is_string($value) => self::string($value),
            is_array($value) => '['.implode(',', array_map(self::write(...), $value)).']',
            $value instanceof stdClass => self::object($value),
            default => throw new InvalidArgumentException('Not a JSON value: '.get_debug_type($value)),
        };
    }

    private static function object(stdClass $object): string
    {
        $members = get_object_vars($object);
        $keys = array_map('strval', array_keys($members));
        usort($keys, Js::compareStrings(...));

        return '{'.implode(',', array_map(fn (string $key) => self::string($key).':'.self::write($members[$key]), $keys)).'}';
    }

    private static function string(string $text): string
    {
        return '"'.preg_replace_callback('/["\\\\\x00-\x1F]/', fn ($m) => match ($m[0]) {
            '"' => '\\"',
            '\\' => '\\\\',
            "\x08" => '\\b',
            "\x0C" => '\\f',
            "\n" => '\\n',
            "\r" => '\\r',
            "\t" => '\\t',
            default => sprintf('\\u%04x', ord($m[0])),
        }, $text).'"';
    }
}
