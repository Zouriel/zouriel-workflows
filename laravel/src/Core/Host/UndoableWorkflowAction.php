<?php

namespace Zouriel\Workflows\Core\Host;

/**
 * An action that can take back what it did, so a reset can undo it (spec §6 reset). It may be asked
 * again for the same run when the unit is retried; the idempotency key tells the calls apart. Port of
 * .NET `IUndoableWorkflowAction`.
 */
interface UndoableWorkflowAction extends WorkflowAction
{
    public function undo(ActionUndoContext $context): void;
}
