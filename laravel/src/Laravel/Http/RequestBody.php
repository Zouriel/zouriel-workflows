<?php

namespace Zouriel\Workflows\Laravel\Http;

use Illuminate\Http\Request;
use JsonException;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * A request body read the way .NET's `WorkflowApi` reads typed bodies (System.Text.Json web defaults): size-capped
 * before parsing, member names matched without regard to case, unknown members ignored, numbers accepted as text,
 * and a member of the wrong type refused as `invalidJson`. Objects stay `stdClass` (`zouriel/forms` values), so `{}`
 * never turns into `[]`.
 */
final class RequestBody
{
    private function __construct(private readonly ?stdClass $members) {}

    /** The raw body as text, refused with `tooLarge` once it passes `$limit` bytes (whatever Content-Length claimed). */
    public static function text(Request $request, int $limit): string
    {
        if ((int) $request->header('Content-Length', '0') > $limit || strlen($text = $request->getContent()) > $limit) {
            throw self::tooLarge($limit);
        }

        return $text;
    }

    /** The body as JSON text, checked to be one JSON value (definitions go to the engine as text). */
    public static function jsonText(Request $request, int $limit): string
    {
        $text = self::text($request, $limit);
        Json::decode($text);

        return $text;
    }

    /** A required typed body: an empty body or `null` is `invalidJson`. */
    public static function required(Request $request, int $limit): self
    {
        $body = self::optional($request, $limit);
        if ($body->members === null) {
            throw new WorkflowException('invalidJson', 'The request needs a JSON body.');
        }

        return $body;
    }

    /** An optional typed body: an empty body (or `null`) reads as no members. */
    public static function optional(Request $request, int $limit): self
    {
        $text = self::text($request, $limit);
        if (trim($text) === '') {
            return new self(null);
        }
        $value = Json::decode($text);
        if ($value !== null && ! $value instanceof stdClass) {
            throw new JsonException('The body must be a JSON object.');
        }

        return new self($value);
    }

    /** Whether the body had members (it was not empty or `null`). */
    public function present(): bool
    {
        return $this->members !== null;
    }

    /** Any JSON value (a .NET `JsonNode`), or null. */
    public function value(string $name): mixed
    {
        if ($this->members === null) {
            return null;
        }
        if (property_exists($this->members, $name)) {
            return $this->members->{$name};
        }
        foreach (get_object_vars($this->members) as $key => $item) {
            if (strcasecmp((string) $key, $name) === 0) {
                return $item;
            }
        }

        return null;
    }

    public function string(string $name): ?string
    {
        $value = $this->value($name);

        return $value === null || is_string($value) ? $value : throw self::wrongType($name, 'text');
    }

    /** An `int` member: a whole number, or text holding one; null when absent. */
    public function int(string $name): ?int
    {
        $value = $this->value($name);
        if (is_string($value) && preg_match('/^-?\d{1,10}$/', $value)) {
            $value = (float) $value;
        }
        if ($value === null) {
            return null;
        }
        if (! is_float($value) || floor($value) !== $value || abs($value) > 2147483647) {
            throw self::wrongType($name, 'a whole number');
        }

        return (int) $value;
    }

    public function bool(string $name): ?bool
    {
        $value = $this->value($name);

        return $value === null || is_bool($value) ? $value : throw self::wrongType($name, 'true or false');
    }

    /** A JSON object member (a .NET `Dictionary<string, JsonNode?>`), or null. */
    public function object(string $name): ?stdClass
    {
        $value = $this->value($name);

        return $value === null || $value instanceof stdClass ? $value : throw self::wrongType($name, 'an object');
    }

    /** @return list<string>|null */
    public function strings(string $name): ?array
    {
        $value = $this->value($name);
        if ($value === null) {
            return null;
        }
        if (! is_array($value) || array_any($value, fn ($item) => ! is_string($item))) {
            throw self::wrongType($name, 'a list of texts');
        }

        return $value;
    }

    /** @return array<string, string>|null an object whose values are all text */
    public function stringMap(string $name): ?array
    {
        $value = $this->object($name);
        if ($value === null) {
            return null;
        }
        $map = get_object_vars($value);
        if (array_any($map, fn ($item) => ! is_string($item))) {
            throw self::wrongType($name, 'an object of texts');
        }

        return $map;
    }

    /** An object member read as a body of its own, or null when absent. */
    public function nested(string $name): ?self
    {
        $value = $this->object($name);

        return $value === null ? null : new self($value);
    }

    /** @return list<self>|null a list of objects, each read as a body of its own */
    public function objects(string $name): ?array
    {
        $value = $this->value($name);
        if ($value === null) {
            return null;
        }
        if (! is_array($value) || array_any($value, fn ($item) => $item !== null && ! $item instanceof stdClass)) {
            throw self::wrongType($name, 'a list of objects');
        }

        return array_map(fn (?stdClass $item) => new self($item), $value);
    }

    public static function tooLarge(int $limit): WorkflowException
    {
        $megabytes = rtrim(rtrim(number_format($limit / (1024 * 1024), 1, '.', ''), '0'), '.');

        return new WorkflowException('tooLarge', "The request is too large (at most {$megabytes} MB).");
    }

    private static function wrongType(string $name, string $expected): JsonException
    {
        return new JsonException("'{$name}' must be {$expected}.");
    }
}
