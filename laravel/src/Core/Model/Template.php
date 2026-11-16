<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\JsonShapeException;

/**
 * Text with token parts (spec §3): strings and `{ref}` parts. A plain string means `[string]`. Port
 * of .NET `Template` and `TemplateConverter`: parts that are neither are dropped.
 */
final class Template
{
    /** @param  list<string|TemplateRef>  $parts */
    public function __construct(public array $parts = []) {}

    public static function of(string $text): self
    {
        return new self([$text]);
    }

    public static function fromJson(mixed $value, string $path): ?self
    {
        if ($value === null) {
            return null;
        }
        if (is_string($value)) {
            return self::of($value);
        }
        if (! is_array($value)) {
            throw new JsonShapeException("A template must be a string or an array. Path: {$path}.");
        }
        $parts = [];
        foreach ($value as $part) {
            if (is_string($part)) {
                $parts[] = $part;
            } elseif ($part instanceof stdClass && is_string($part->ref ?? null)) {
                $parts[] = new TemplateRef($part->ref);
            }
        }

        return new self($parts);
    }

    /** @return list<mixed> */
    public function toJson(): array
    {
        return array_map(fn ($p) => $p instanceof TemplateRef ? (object) ['ref' => $p->ref] : $p, $this->parts);
    }

    /** @return list<string> the token paths of the template's parts */
    public function refs(): array
    {
        return array_values(array_map(fn (TemplateRef $p) => $p->ref, array_filter($this->parts, fn ($p) => $p instanceof TemplateRef)));
    }
}
