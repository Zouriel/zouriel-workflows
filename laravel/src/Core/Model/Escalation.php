<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Adds assignees to an open task after a time, keeping the original ones. Port of .NET `Escalation`. */
final class Escalation
{
    /** ISO duration from the step's start. */
    public string $after = '';

    /** @var list<Assignee> */
    public array $assignees = [];

    /** False: the added people are not notified; null = notified. */
    public ?bool $notify = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $escalation = new self;
        $escalation->after = Read::string($value, 'after', $path);
        $escalation->assignees = Assignee::list($value, 'assignees', $path) ?? [];
        $escalation->notify = Read::optionalBool($value, 'notify', $path);

        return $escalation;
    }

    public function toJson(): stdClass
    {
        return Write::object(['after' => $this->after, 'assignees' => Write::list($this->assignees), 'notify' => $this->notify]);
    }
}
