<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/**
 * Starting a record's workflow over (spec §6): the people in `who` may move the record back to `to`
 * (the initial state by default), then `steps` run. Port of .NET `LifecycleReset`.
 */
final class LifecycleReset
{
    /** The button text (default "Reset workflow"). */
    public ?string $label = null;

    /** The state id to go back to; null = the initial state. */
    public ?string $to = null;

    /** @var list<Assignee>|null who may reset by hand; null or empty = nobody */
    public ?array $who = null;

    public ?string $confirm = null;

    public ?TransitionInput $input = null;

    /** Run the current state's `onExit` and the target's `onEnter` (default false). */
    public ?bool $runExitEnter = null;

    /** Also cancel running automations about the record (default true). */
    public ?bool $cancelAutomations = null;

    /** `none`, `optional` (default) or `required`. */
    public ?string $comment = null;

    /** @var list<string>|null the state ids the reset is offered in; null = every state */
    public ?array $states = null;

    /** @var list<Step>|null run after the record is back (list id `reset`) */
    public ?array $steps = null;

    /** The remarks setting with its default applied. */
    public function resolvedComment(): string
    {
        return Remarks::resolve($this->comment, Remarks::OPTIONAL);
    }

    public static function fromJson(stdClass $value, string $path): self
    {
        $reset = new self;
        $reset->label = Read::optionalString($value, 'label', $path);
        $reset->to = Read::optionalString($value, 'to', $path);
        $reset->who = Assignee::list($value, 'who', $path);
        $reset->confirm = Read::optionalString($value, 'confirm', $path);
        $reset->input = Read::model($value, 'input', $path, 'Zouriel.Workflows.Model.TransitionInput', TransitionInput::fromJson(...));
        $reset->runExitEnter = Read::optionalBool($value, 'runExitEnter', $path);
        $reset->cancelAutomations = Read::optionalBool($value, 'cancelAutomations', $path);
        $reset->comment = Read::optionalString($value, 'comment', $path);
        $reset->states = Read::strings($value, 'states', $path);
        $reset->steps = Step::list($value, 'steps', $path);

        return $reset;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'label' => $this->label, 'to' => $this->to, 'who' => Write::list($this->who), 'confirm' => $this->confirm,
            'input' => $this->input?->toJson(), 'runExitEnter' => $this->runExitEnter, 'cancelAutomations' => $this->cancelAutomations,
            'comment' => $this->comment, 'states' => $this->states, 'steps' => Write::list($this->steps),
        ]);
    }
}
