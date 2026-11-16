<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** When a `parallel` step finishes: `all`, `any` or `count` paths. Port of .NET `ParallelJoin`. */
final class ParallelJoin
{
    public string $type = 'all';

    public ?int $count = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $join = new self;
        $join->type = Read::string($value, 'type', $path, 'all');
        $join->count = Read::optionalInt($value, 'count', $path);

        return $join;
    }

    public function toJson(): stdClass
    {
        return Write::object(['type' => $this->type, 'count' => $this->count]);
    }
}
