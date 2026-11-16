<?php

namespace Zouriel\Workflows\Core\Changes;

use Zouriel\Workflows\Core\WorkflowException;

/** Raised from the change hook when code writes a field a lifecycle owns (enforce mode, spec §6). Port of .NET `WorkflowFieldOwnedException`. */
final class FieldOwnedException extends WorkflowException
{
    public function __construct(public readonly string $entity, public readonly string $field, public readonly string $lifecycle)
    {
        parent::__construct('fieldOwned', "'{$entity}.{$field}' is controlled by the '{$lifecycle}' workflow; change it by applying a transition.");
    }
}
