<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/**
 * One thing a workflow did that the reset list shows (spec §6 reset): `kind` is `field`, `created`, `action`,
 * `message`, `webhook` or `move`; `selected` is whether it is ticked by default. Port of .NET `ResetItem`.
 */
final readonly class ResetItem
{
    public function __construct(
        public string $id,
        public string $kind,
        public string $title,
        public ?string $detail,
        public string $workflow,
        public string $step,
        public bool $undoable,
        public bool $selected,
        public ?string $warning,
    ) {}

    public function with(string $title, ?string $detail, ?string $warning): self
    {
        return new self($this->id, $this->kind, $title, $detail, $this->workflow, $this->step, $this->undoable, $this->selected, $warning);
    }
}
