<?php

namespace Zouriel\Workflows\Core\Lint;

use stdClass;

/**
 * One problem in a definition. Port of .NET `Lint/Linter.cs` `LintProblem`.
 *
 * @param  string  $code  stable problem code, the same in every engine and editor (`spec/fixtures/lint`)
 * @param  string  $severity  `error` (blocks publishing) or `warning`
 * @param  ?string  $node  the id of the step, state or transition the problem is on; null for the whole definition
 */
final readonly class LintProblem
{
    public function __construct(public string $code, public string $severity, public ?string $node, public string $message) {}

    public function isError(): bool
    {
        return $this->severity === 'error';
    }

    /** The API shape (spec/api/openapi.json `LintProblem`: every member, `node` null when it has none). */
    public function toJson(): stdClass
    {
        return (object) ['code' => $this->code, 'severity' => $this->severity, 'node' => $this->node, 'message' => $this->message];
    }
}
