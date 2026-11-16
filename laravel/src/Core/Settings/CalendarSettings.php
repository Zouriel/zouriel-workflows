<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** The working calendar (spec §9). Port of .NET `Settings/GlobalSettings.cs` `CalendarSettings`. */
final class CalendarSettings
{
    /** @var list<string>|null `mon`…`sun`; null = Monday to Friday */
    public ?array $days = null;

    /** @var list<string>|null `["09:00", "17:00"]` */
    public ?array $hours = null;

    /** @var list<string>|null non-working dates `YYYY-MM-DD` */
    public ?array $holidays = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $calendar = new self;
        $calendar->days = Read::strings($value, 'days', $path);
        $calendar->hours = Read::strings($value, 'hours', $path);
        $calendar->holidays = Read::strings($value, 'holidays', $path);

        return $calendar;
    }

    public function toJson(): stdClass
    {
        return Write::object(['days' => $this->days, 'hours' => $this->hours, 'holidays' => $this->holidays]);
    }
}
