<?php

namespace Zouriel\Workflows\Core;

use RuntimeException;

/**
 * An engine error with a stable code (spec/api/openapi.json `WorkflowError`), e.g. `badDuration`,
 * `notAllowed`, and optional details for the caller (lint problems, form errors). Port of .NET
 * `Abstractions.cs` `WorkflowException`.
 */
class WorkflowException extends RuntimeException
{
    public function __construct(public readonly string $errorCode, string $message, public readonly mixed $details = null)
    {
        parent::__construct($message);
    }
}
