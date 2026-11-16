<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Forms\Values;
use Zouriel\Workflows\Core\Json\JsonShapeException;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/**
 * `{const}`, `{ref}`, `{today}` or `{calc}` (spec §3). Port of .NET `Model/Definition.cs` `Operand`
 * and `OperandConverter` (`Model/Converters.cs`): the first of const, ref, today, calc present wins;
 * a stored `{}` is an unfinished operand.
 */
final class Operand
{
    public bool $hasConst = false;

    /** The constant, as a workflow value. */
    public mixed $const = null;

    public ?string $ref = null;

    /** Days from today. */
    public ?int $today = null;

    public ?Calc $calc = null;

    public static function of(mixed $value): self
    {
        $operand = new self;
        $operand->hasConst = true;
        $operand->const = Json::normalize($value);

        return $operand;
    }

    public static function fromJson(mixed $value, string $path): self
    {
        if (! $value instanceof stdClass) {
            throw new JsonShapeException("An operand must be an object. Path: {$path}.");
        }
        $operand = new self;
        if (property_exists($value, 'const')) {
            $operand->hasConst = true;
            $operand->const = Json::normalize($value->const);
        } elseif (is_scalar($value->ref ?? null)) {
            $operand->ref = Read::string($value, 'ref', $path);
        } elseif (is_scalar($value->today ?? null)) {
            $today = $value->today;
            $operand->today = is_float($today) && is_finite($today) ? (int) $today : Read::fail("{$path}.today", 'System.Double');
        } elseif (($value->calc ?? null) instanceof stdClass) {
            $operand->calc = Calc::fromJson($value->calc, "{$path}.calc");
        }

        return $operand;
    }

    public function toJson(): stdClass
    {
        return match (true) {
            $this->hasConst => (object) ['const' => $this->const],
            $this->ref !== null => (object) ['ref' => $this->ref],
            $this->today !== null => (object) ['today' => $this->today],
            $this->calc !== null => (object) ['calc' => $this->calc->toJson()],
            default => new stdClass,
        };
    }

    /** Whether the operand has what it needs (spec §4 "unfinished"): a path, a day offset, a calculation or a non-empty constant. */
    public function isFilled(): bool
    {
        return ($this->ref !== null && $this->ref !== '') || $this->today !== null || $this->calc !== null
            || ($this->hasConst && ! Values::isEmpty($this->const));
    }

    /** @return list<string> every token path the operand reads */
    public function refs(): array
    {
        return array_merge($this->ref !== null && $this->ref !== '' ? [$this->ref] : [], $this->calc?->refs() ?? []);
    }

    /** @param  array<string, self>|null  $operands */
    public static function mapToJson(?array $operands): ?stdClass
    {
        return Write::map($operands);
    }
}
