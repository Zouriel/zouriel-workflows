<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Makes a schedule start one instance per record of the workflow's entity. Port of .NET `ScheduleForEach`. */
final class ScheduleForEach
{
    /** Only records matching this get an instance; null = all. */
    public ?Condition $when = null;

    /** The most instances one run of the schedule starts (default 100, at most 500). */
    public int $limit = 100;

    public static function fromJson(stdClass $value, string $path): self
    {
        $forEach = new self;
        $forEach->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");
        $forEach->limit = Read::int($value, 'limit', $path, 100);

        return $forEach;
    }

    public function toJson(): stdClass
    {
        return Write::object(['when' => $this->when?->toJson(), 'limit' => $this->limit]);
    }
}
