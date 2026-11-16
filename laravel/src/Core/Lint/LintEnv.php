<?php

namespace Zouriel\Workflows\Core\Lint;

use Zouriel\Workflows\Core\Model\Step;
use Zouriel\Workflows\Core\Model\Trigger;

/**
 * What a step can read where it stands (.NET `Linter.Run.Env`).
 *
 * @internal
 *
 * @param  ?string  $entity  the record type `entity.*` reads
 * @param  ?string  $itemEntity  inside forEach: the record type `item.*` reads
 * @param  list<Step>  $upstream  steps whose outputs are available
 * @param  list<string>  $enclosingLists  the step lists a `goTo` here may jump back into
 */
final readonly class LintEnv
{
    public function __construct(
        public ?string $entity,
        public ?string $itemEntity,
        public array $upstream,
        public bool $inputAllowed,
        public ?Trigger $trigger,
        public bool $inParallelOrLoop = false,
        public array $enclosingLists = [],
    ) {}

    /** A copy with some members changed (C# `with`). */
    public function with(mixed ...$changes): self
    {
        return new self(...array_merge(get_object_vars($this), $changes));
    }

    /** Inside a container step: the container's outputs are available. */
    public function inside(Step $container): self
    {
        return $this->with(upstream: [...$this->upstream, $container]);
    }
}
