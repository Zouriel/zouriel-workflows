<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A service-level timer of a state: run steps once after `within`, with its pause rules (spec §6). Port of .NET `Sla`. */
final class Sla
{
    /** ISO duration the record may stay in the state. */
    public string $within = '';

    /** `working` to count working time only; null = calendar time. */
    public ?string $calendar = null;

    /** @var list<Step> */
    public array $steps = [];

    /** @var list<string>|null state ids the record can be in (instead of this state) with the clock kept and paused */
    public ?array $pauseOnStatus = null;

    /** @var list<string>|null signal keys about the record that pause the clock */
    public ?array $pauseOnEvent = null;

    /** @var list<string>|null signal keys about the record that end an event pause */
    public ?array $resumeOnEvent = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $sla = new self;
        $sla->within = Read::string($value, 'within', $path);
        $sla->calendar = Read::optionalString($value, 'calendar', $path);
        $sla->steps = Step::list($value, 'steps', $path) ?? [];
        $sla->pauseOnStatus = Read::strings($value, 'pauseOnStatus', $path);
        $sla->pauseOnEvent = Read::strings($value, 'pauseOnEvent', $path);
        $sla->resumeOnEvent = Read::strings($value, 'resumeOnEvent', $path);

        return $sla;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'within' => $this->within, 'calendar' => $this->calendar, 'steps' => Write::list($this->steps),
            'pauseOnStatus' => $this->pauseOnStatus, 'pauseOnEvent' => $this->pauseOnEvent, 'resumeOnEvent' => $this->resumeOnEvent,
        ]);
    }
}
