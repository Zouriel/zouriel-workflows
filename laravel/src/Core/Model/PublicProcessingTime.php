<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** The processing time shown to the public (spec §1): stored with the definition, no behaviour. Port of .NET `PublicProcessingTime`. */
final class PublicProcessingTime
{
    /** Text people outside the office read (at most 200 characters). */
    public string $estimate = '';

    /** Whether the app may show it to the public. */
    public bool $display = false;

    public static function fromJson(stdClass $value, string $path): self
    {
        $time = new self;
        $time->estimate = Read::string($value, 'estimate', $path);
        $time->display = Read::bool($value, 'display', $path);

        return $time;
    }

    public function toJson(): stdClass
    {
        return (object) ['estimate' => $this->estimate, 'display' => $this->display];
    }
}
