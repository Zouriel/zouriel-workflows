<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Automatic application of a transition: after a time in a `from` state, or when a condition holds. Port of .NET `AutoTransition`. */
final class AutoTransition
{
    public ?string $after = null;

    public ?string $calendar = null;

    public ?Condition $when = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $auto = new self;
        $auto->after = Read::optionalString($value, 'after', $path);
        $auto->calendar = Read::optionalString($value, 'calendar', $path);
        $auto->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");

        return $auto;
    }

    public function toJson(): stdClass
    {
        return Write::object(['after' => $this->after, 'calendar' => $this->calendar, 'when' => $this->when?->toJson()]);
    }
}
