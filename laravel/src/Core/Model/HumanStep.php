<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Shared by `ApprovalStep` and `TaskStep`: who decides, when it is due, reminders, escalation, form. Port of .NET `HumanStep`. */
abstract class HumanStep extends Step
{
    /** @var list<Assignee> resolved when the step starts (none fails it with `noAssignees`) */
    public array $assignees = [];

    /** ISO duration until the task times out (required). */
    public string $due = '';

    /** `working` to count working time only; null = calendar time. */
    public ?string $calendar = null;

    /** @var list<string>|null ISO durations after which undecided assignees are reminded; null uses the settings default */
    public ?array $reminders = null;

    public ?Escalation $escalation = null;

    /** A form-builder form each decision fills in (output `data`). */
    public ?stdClass $form = null;

    public ?Template $instructions = null;

    /** Null uses the settings default. */
    public ?bool $allowDelegation = null;

    /** `never`, `reject` or `always`. */
    public ?string $requireComment = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->assignees = Assignee::list($value, 'assignees', $path) ?? [];
        $this->due = Read::string($value, 'due', $path);
        $this->calendar = Read::optionalString($value, 'calendar', $path);
        $this->reminders = Read::strings($value, 'reminders', $path);
        $this->escalation = Read::model($value, 'escalation', $path, 'Zouriel.Workflows.Model.Escalation', Escalation::fromJson(...));
        $this->form = Read::object($value, 'form', $path);
        $this->instructions = Template::fromJson(Read::value($value, 'instructions'), "{$path}.instructions");
        $this->allowDelegation = Read::optionalBool($value, 'allowDelegation', $path);
        $this->requireComment = Read::optionalString($value, 'requireComment', $path);
    }

    protected function members(): array
    {
        return [
            'assignees' => Write::list($this->assignees), 'due' => $this->due, 'calendar' => $this->calendar, 'reminders' => $this->reminders,
            'escalation' => $this->escalation?->toJson(), 'form' => $this->form, 'instructions' => $this->instructions?->toJson(),
            'allowDelegation' => $this->allowDelegation, 'requireComment' => $this->requireComment,
        ];
    }
}
