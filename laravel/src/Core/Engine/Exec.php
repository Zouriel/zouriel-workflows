<?php

namespace Zouriel\Workflows\Core\Engine;

use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\StepRecord;

/** One instance being driven inside a unit of work. Port of .NET `WorkflowEngine.Run.cs` `Exec`. */
final class Exec
{
    public bool $ended = false;

    /** The step record of the step being executed right now (so a thrown error can close it). */
    public ?StepRecord $current = null;

    public function __construct(public readonly Uow $u, public readonly InstanceRecord $i, public readonly CompiledDefinition $c, public readonly RunState $s) {}

    public function actor(): WorkflowActor
    {
        return WorkflowActor::workflow($this->i->definitionKey, $this->i->id);
    }

    public function scope(?Token $t = null): ValueScope
    {
        return $this->u->scope($this->i, $this->s, $t);
    }
}
