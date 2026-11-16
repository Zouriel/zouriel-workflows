<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** How tasks move between people. Port of .NET `PeopleSettings`. */
final class PeopleSettings
{
    /** Whether tasks may be delegated when a step does not say. */
    public ?bool $delegation = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $people = new self;
        $people->delegation = Read::optionalBool($value, 'delegation', $path);

        return $people;
    }

    public function toJson(): stdClass
    {
        return Write::object(['delegation' => $this->delegation]);
    }
}
