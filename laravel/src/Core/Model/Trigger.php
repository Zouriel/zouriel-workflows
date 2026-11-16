<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** What starts an automation (spec §5); which members apply depends on `type`. Port of .NET `Trigger`. */
final class Trigger
{
    /** One of `TriggerTypes`. */
    public string $type = '';

    /** Extra condition checked against the record after the change; null always holds. */
    public ?Condition $when = null;

    /** True: the workflow's own writes may start it again. */
    public ?bool $retrigger = null;

    /** `fieldChanged` and `dateReached`: the field's key. */
    public ?string $field = null;

    /** @var list<mixed>|null `fieldChanged`: the old value must be one of these */
    public ?array $from = null;

    /** @var list<mixed>|null `fieldChanged`: the new value must be one of these */
    public ?array $to = null;

    /** `stateEntered`/`transitionApplied`: the lifecycle definition key. */
    public ?string $lifecycle = null;

    /** @var list<string>|null */
    public ?array $states = null;

    /** @var list<string>|null */
    public ?array $transitions = null;

    /** `dateReached`: signed ISO duration added to the date. */
    public ?string $offset = null;

    /** `dateReached` on a date field: time of day `HH:MM`. */
    public ?string $at = null;

    /** `schedule`: a five-field cron expression. */
    public ?string $cron = null;

    public ?ScheduleForEach $forEach = null;

    /** `manual`: the button text. */
    public ?string $label = null;

    /** `manual`: an optional form-builder form (answers are `input.*`). */
    public ?stdClass $form = null;

    /** @var list<Assignee>|null `manual`: who may start it; null or empty = anyone signed in */
    public ?array $who = null;

    /** `signal`: the registered signal key. */
    public ?string $signal = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $trigger = new self;
        $trigger->type = Read::string($value, 'type', $path);
        $trigger->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");
        $trigger->retrigger = Read::optionalBool($value, 'retrigger', $path);
        $trigger->field = Read::optionalString($value, 'field', $path);
        $trigger->from = Read::list($value, 'from', $path, fn ($v) => $v);
        $trigger->to = Read::list($value, 'to', $path, fn ($v) => $v);
        $trigger->lifecycle = Read::optionalString($value, 'lifecycle', $path);
        $trigger->states = Read::strings($value, 'states', $path);
        $trigger->transitions = Read::strings($value, 'transitions', $path);
        $trigger->offset = Read::optionalString($value, 'offset', $path);
        $trigger->at = Read::optionalString($value, 'at', $path);
        $trigger->cron = Read::optionalString($value, 'cron', $path);
        $trigger->forEach = Read::model($value, 'forEach', $path, 'Zouriel.Workflows.Model.ScheduleForEach', ScheduleForEach::fromJson(...));
        $trigger->label = Read::optionalString($value, 'label', $path);
        $trigger->form = Read::object($value, 'form', $path);
        $trigger->who = Assignee::list($value, 'who', $path);
        $trigger->signal = Read::optionalString($value, 'signal', $path);

        return $trigger;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'type' => $this->type, 'when' => $this->when?->toJson(), 'retrigger' => $this->retrigger, 'field' => $this->field,
            'from' => $this->from, 'to' => $this->to, 'lifecycle' => $this->lifecycle, 'states' => $this->states,
            'transitions' => $this->transitions, 'offset' => $this->offset, 'at' => $this->at, 'cron' => $this->cron,
            'forEach' => $this->forEach?->toJson(), 'label' => $this->label, 'form' => $this->form, 'who' => Write::list($this->who),
            'signal' => $this->signal,
        ]);
    }
}
