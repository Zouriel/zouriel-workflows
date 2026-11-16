<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Applies a lifecycle transition to the record or a related one (output `applied`). Port of .NET `ApplyTransitionStep`. */
final class ApplyTransitionStep extends Step
{
    /** `self`/null for the workflow's record, or a token path to a relation. */
    public ?string $target = null;

    /** The lifecycle definition key. */
    public string $lifecycle = '';

    /** The transition key. */
    public string $transition = '';

    /** @var array<string, Operand>|null answers for the transition's input form, by key */
    public ?array $input = null;

    /** Remarks recorded with the move (`trigger.comment`). */
    public ?Template $comment = null;

    /** `fail` (default) or `skip`. */
    public ?string $ifNotAllowed = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->target = Read::optionalString($value, 'target', $path);
        $this->lifecycle = Read::string($value, 'lifecycle', $path);
        $this->transition = Read::string($value, 'transition', $path);
        $this->input = Read::map($value, 'input', $path, Operand::fromJson(...));
        $this->comment = Template::fromJson(Read::value($value, 'comment'), "{$path}.comment");
        $this->ifNotAllowed = Read::optionalString($value, 'ifNotAllowed', $path);
    }

    protected function members(): array
    {
        return [
            'target' => $this->target, 'lifecycle' => $this->lifecycle, 'transition' => $this->transition, 'input' => Write::map($this->input),
            'comment' => $this->comment?->toJson(), 'ifNotAllowed' => $this->ifNotAllowed,
        ];
    }
}
