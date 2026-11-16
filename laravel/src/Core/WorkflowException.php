<?php

namespace Zouriel\Workflows\Core;

use RuntimeException;

/**
 * An engine error with a stable code (spec/api/openapi.json `WorkflowError`), e.g. `badDuration`,
 * `notAllowed`. Port of .NET `WorkflowException.cs`.
 */
class WorkflowException extends RuntimeException
{
    public function __construct(public readonly string $errorCode, string $message)
    {
        parent::__construct($message);
    }
}
