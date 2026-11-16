<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A move between lifecycle states (spec §6): who may apply it, its guard, input form and steps. Port of .NET `Transition`. */
final class Transition
{
    /** Immutable id (auto timers refer to it). */
    public string $id = '';

    /** The key people and APIs apply it by. */
    public string $key = '';

    /** The button text. */
    public string $label = '';

    public TransitionFrom $from;

    /** The state id it leads to. */
    public string $to = '';

    /** @var list<Assignee>|null who may apply it by hand; null or empty = nobody */
    public ?array $who = null;

    /** Guard: when false the transition is neither offered nor applicable. */
    public ?Condition $when = null;

    public ?TransitionInput $input = null;

    /** @var list<Step>|null run as a child run after the state changes */
    public ?array $steps = null;

    public ?AutoTransition $auto = null;

    public ?string $confirm = null;

    public ?string $tone = null;

    /** True: people never see or apply it; only automations and the system do. */
    public ?bool $hidden = null;

    /** `none`, `optional` or `required`; null = optional for `danger` moves, else none. */
    public ?string $comment = null;

    public function __construct()
    {
        $this->from = new TransitionFrom;
    }

    /** The remarks setting with its default applied. */
    public function resolvedComment(): string
    {
        return Remarks::resolve($this->comment, $this->tone === 'danger' ? Remarks::OPTIONAL : Remarks::NONE);
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.Transition');
        $transition = new self;
        $transition->id = Read::string($value, 'id', $path);
        $transition->key = Read::string($value, 'key', $path);
        $transition->label = Read::string($value, 'label', $path);
        $transition->from = TransitionFrom::fromJson(Read::value($value, 'from'), "{$path}.from");
        $transition->to = Read::string($value, 'to', $path);
        $transition->who = Assignee::list($value, 'who', $path);
        $transition->when = Condition::fromJson(Read::value($value, 'when'), "{$path}.when");
        $transition->input = Read::model($value, 'input', $path, 'Zouriel.Workflows.Model.TransitionInput', TransitionInput::fromJson(...));
        $transition->steps = Step::list($value, 'steps', $path);
        $transition->auto = Read::model($value, 'auto', $path, 'Zouriel.Workflows.Model.AutoTransition', AutoTransition::fromJson(...));
        $transition->confirm = Read::optionalString($value, 'confirm', $path);
        $transition->tone = Read::optionalString($value, 'tone', $path);
        $transition->hidden = Read::optionalBool($value, 'hidden', $path);
        $transition->comment = Read::optionalString($value, 'comment', $path);

        return $transition;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'id' => $this->id, 'key' => $this->key, 'label' => $this->label, 'from' => $this->from->toJson(), 'to' => $this->to,
            'who' => Write::list($this->who), 'when' => $this->when?->toJson(), 'input' => $this->input?->toJson(),
            'steps' => Write::list($this->steps), 'auto' => $this->auto?->toJson(), 'confirm' => $this->confirm, 'tone' => $this->tone,
            'hidden' => $this->hidden, 'comment' => $this->comment,
        ]);
    }
}
