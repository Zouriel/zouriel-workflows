<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Settings of one workflow that override the global defaults (spec §12). Port of .NET `WorkflowSettings`. */
final class WorkflowSettings
{
    /** `perRecordSerial`, `parallel` or `skipIfRunning`; null uses the global default. */
    public ?string $concurrency = null;

    /** @var list<Assignee>|null people responsible for the workflow */
    public ?array $owners = null;

    public ?bool $notifyOwnersOnIncident = null;

    /** `working` or `calendar` (null = calendar time). */
    public ?string $calendar = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $settings = new self;
        $settings->concurrency = Read::optionalString($value, 'concurrency', $path);
        $settings->owners = Assignee::list($value, 'owners', $path);
        $settings->notifyOwnersOnIncident = Read::optionalBool($value, 'notifyOwnersOnIncident', $path);
        $settings->calendar = Read::optionalString($value, 'calendar', $path);

        return $settings;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'concurrency' => $this->concurrency, 'owners' => Write::list($this->owners),
            'notifyOwnersOnIncident' => $this->notifyOwnersOnIncident, 'calendar' => $this->calendar,
        ]);
    }
}
