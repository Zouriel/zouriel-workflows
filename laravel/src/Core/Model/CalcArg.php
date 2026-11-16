<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\JsonShapeException;
use Zouriel\Workflows\Core\Json\Read;

/** One argument of a calculation: a token path, a constant or a nested calculation. Port of .NET `CalcArg` and `CalcArgConverter`. */
final class CalcArg
{
    public ?string $ref = null;

    public bool $hasConst = false;

    public mixed $const = null;

    public ?Calc $calc = null;

    public static function fromJson(mixed $value, string $path): self
    {
        if (! $value instanceof stdClass) {
            throw new JsonShapeException("A calculation argument must be an object. Path: {$path}.");
        }
        $arg = new self;
        if (is_scalar($value->ref ?? null)) {
            $arg->ref = Read::string($value, 'ref', $path);
        } elseif (property_exists($value, 'const')) {
            $arg->hasConst = true;
            $arg->const = Json::normalize($value->const);
        } elseif (($value->calc ?? null) instanceof stdClass) {
            $arg->calc = Calc::fromJson($value->calc, "{$path}.calc");
        }

        return $arg;
    }

    public function toJson(): stdClass
    {
        return match (true) {
            $this->ref !== null => (object) ['ref' => $this->ref],
            $this->hasConst => (object) ['const' => $this->const],
            $this->calc !== null => (object) ['calc' => $this->calc->toJson()],
            default => new stdClass,
        };
    }
}
