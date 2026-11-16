<?php

namespace Zouriel\Workflows\Core\Audit;

use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Registry\FieldDescriptor;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\GlobalSettings;

/**
 * Masks sensitive values wherever data leaves the engine: audit entries, run and task views, test runs
 * (spec §11). A value is masked under a sensitive key (a registry field marked `sensitive`, or listed in
 * `settings.audit.maskedFields`), or when it equals (or, inside text, contains) the current value of a
 * sensitive field of the record in question. Port of .NET `Audit/Masking.cs`.
 */
final class Masker
{
    public const string HIDDEN = '***';

    /** Text shorter than this is never matched by value (it would hide unrelated short values). */
    private const int MIN_VALUE_LENGTH = 4;

    /**
     * @param  array<string, true>  $keys
     * @param  list<string>  $values  longest first
     */
    private function __construct(private readonly array $keys, private readonly array $values) {}

    public static function none(): self
    {
        return new self([], []);
    }

    /** The sensitive keys of a registry and settings, and the sensitive values of one record (if given). */
    public static function create(WorkflowRegistry $registry, GlobalSettings $settings, ?stdClass $record = null, ?string $entity = null): self
    {
        $values = [];
        if ($record !== null && $entity !== null) {
            foreach ($registry->entity($entity)->fields ?? [] as $f) {
                if (self::isSensitive($f, $entity, $settings)) {
                    self::collect($record->{$f->key} ?? null, $values);
                }
            }
        }
        $values = array_values(array_unique($values));
        // Longest first (a stable sort, as .NET's OrderByDescending).
        $order = array_map(fn ($v, $i) => [$v, $i], $values, array_keys($values));
        usort($order, fn ($a, $b) => Js::length($b[0]) <=> Js::length($a[0]) ?: $a[1] <=> $b[1]);

        return new self(self::sensitiveKeys($registry, $settings), array_column($order, 0));
    }

    /** @return array<string, true> every key whose value is hidden */
    public static function sensitiveKeys(WorkflowRegistry $registry, GlobalSettings $settings): array
    {
        $keys = [];
        foreach ($registry->entities as $e) {
            foreach ($e->fields as $f) {
                if ($f->isSensitive()) {
                    $keys[$f->key] = true;
                }
            }
        }
        foreach ($settings->audit->maskedFields ?? [] as $m) {
            $dot = strpos($m, '.');
            if ($dot !== false && $dot > 0 && $dot < strlen($m) - 1) {
                $keys[substr($m, $dot + 1)] = true;
            }
        }

        return $keys;
    }

    /** Whether a field's values are hidden: marked `sensitive` in the registry or listed in `settings.audit.maskedFields`. */
    public static function isSensitive(FieldDescriptor $f, string $entity, GlobalSettings $settings): bool
    {
        return $f->isSensitive() || in_array("{$entity}.{$f->key}", $settings->audit->maskedFields ?? [], true);
    }

    /** @param  list<string>  $into */
    private static function collect(mixed $value, array &$into): void
    {
        $value = Json::normalize($value);
        if ($value === null || is_bool($value)) {
            return;
        }
        if (is_array($value) || $value instanceof stdClass) {
            foreach (is_array($value) ? $value : get_object_vars($value) as $item) {
                self::collect($item, $into);
            }

            return;
        }
        $text = Values::text($value);
        if (Js::length($text) < self::MIN_VALUE_LENGTH) {
            return;
        }
        $into[] = $text;
        // Values inserted into webhook paths are URL-encoded ("a@b.c" as "a%40b.c"); hide that form too.
        $encoded = rawurlencode($text);
        if ($encoded !== $text) {
            $into[] = $encoded;
        }
    }

    /** A copy of `$value` with sensitive parts replaced by `***`. */
    public function mask(mixed $value): mixed
    {
        return $this->maskValue(Json::normalize($value));
    }

    /** Masks a trigger value: also hides `from`/`to` when the trigger watched a sensitive field. */
    public function maskTrigger(mixed $trigger): mixed
    {
        $masked = $this->mask($trigger);
        if ($masked instanceof stdClass && is_string($masked->field ?? null) && isset($this->keys[$masked->field])) {
            if (($masked->from ?? null) !== null) {
                $masked->from = self::HIDDEN;
            }
            if (($masked->to ?? null) !== null) {
                $masked->to = self::HIDDEN;
            }
        }

        return $masked;
    }

    /** Masks text: every occurrence of a sensitive value is replaced. */
    public function maskText(?string $text): ?string
    {
        if ($text === null || $this->values === []) {
            return $text;
        }
        foreach ($this->values as $value) {
            $text = str_replace($value, self::HIDDEN, $text);
        }

        return $text;
    }

    private function maskValue(mixed $value): mixed
    {
        if ($value instanceof stdClass) {
            $out = new stdClass;
            foreach (get_object_vars($value) as $key => $item) {
                $out->{$key} = isset($this->keys[(string) $key]) && $item !== null ? self::HIDDEN : $this->maskValue($item);
            }

            return $out;
        }

        return match (true) {
            is_array($value) => array_map($this->maskValue(...), $value),
            is_string($value) => in_array($value, $this->values, true) ? self::HIDDEN : $this->maskText($value),
            is_float($value) && $this->values !== [] && in_array(Js::numberText($value), $this->values, true) => self::HIDDEN,
            default => $value,
        };
    }
}
