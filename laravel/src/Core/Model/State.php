<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A lifecycle state; its key is the field value it stands for. Port of .NET `State`. */
final class State
{
    /** Immutable id that transitions refer to. */
    public string $id = '';

    /** The field value this state stands for. */
    public string $key = '';

    public string $label = '';

    /** `initial` (exactly one), `active` or `terminal`. */
    public string $category = 'active';

    public ?string $tone = null;

    public ?string $description = null;

    /** @var list<Step>|null run as a child run each time the record enters the state */
    public ?array $onEnter = null;

    /** @var list<Step>|null run as a child run each time the record leaves the state */
    public ?array $onExit = null;

    /** True: onEnter and SLA runs keep going after the record leaves the state. */
    public ?bool $keepRunsOnExit = null;

    public ?Sla $sla = null;

    public function isTerminal(): bool
    {
        return $this->category === 'terminal';
    }

    public function isInitial(): bool
    {
        return $this->category === 'initial';
    }

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.State');
        $state = new self;
        $state->id = Read::string($value, 'id', $path);
        $state->key = Read::string($value, 'key', $path);
        $state->label = Read::string($value, 'label', $path);
        $state->category = Read::string($value, 'category', $path, 'active');
        $state->tone = Read::optionalString($value, 'tone', $path);
        $state->description = Read::optionalString($value, 'description', $path);
        $state->onEnter = Step::list($value, 'onEnter', $path);
        $state->onExit = Step::list($value, 'onExit', $path);
        $state->keepRunsOnExit = Read::optionalBool($value, 'keepRunsOnExit', $path);
        $state->sla = Read::model($value, 'sla', $path, 'Zouriel.Workflows.Model.Sla', Sla::fromJson(...));

        return $state;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'id' => $this->id, 'key' => $this->key, 'label' => $this->label, 'category' => $this->category, 'tone' => $this->tone,
            'description' => $this->description, 'onEnter' => Write::list($this->onEnter), 'onExit' => Write::list($this->onExit),
            'keepRunsOnExit' => $this->keepRunsOnExit, 'sla' => $this->sla?->toJson(),
        ]);
    }
}
