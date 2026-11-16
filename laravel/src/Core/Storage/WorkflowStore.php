<?php

namespace Zouriel\Workflows\Core\Storage;

/** Opens units of work; everything inside one session commits or rolls back together. Port of .NET `IWorkflowStore`. */
interface WorkflowStore
{
    /** Opens a unit of work; dropping it without committing rolls everything back. */
    public function begin(): WorkflowSession;
}
