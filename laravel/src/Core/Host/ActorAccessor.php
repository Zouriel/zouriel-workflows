<?php

namespace Zouriel\Workflows\Core\Host;

/** The person behind record writes made outside workflows (the logged-in user). Port of .NET `IWorkflowActorAccessor`. */
interface ActorAccessor
{
    /** The person behind the current request, or null (writes are then attributed to the system). */
    public function current(): ?WorkflowActor;
}
