<?php

namespace Zouriel\Workflows\Core\Lint;

use Zouriel\Workflows\Core\Model\WorkflowDefinition;

/**
 * Checks a definition (spec §13). Errors block publishing; warnings don't. Port of .NET
 * `Lint/Linter.cs`; the codes, severities and nodes are proven by `spec/fixtures/lint`.
 */
final class Linter
{
    /** The label of each step type, used in messages and served to editors (`GET /schema`). */
    public const array STEP_LABELS = [
        'branch' => 'Branch', 'switch' => 'Switch', 'parallel' => 'Parallel paths', 'forEach' => 'For each',
        'approval' => 'Approval', 'task' => 'Task', 'wait' => 'Wait', 'setFields' => 'Update record', 'createRecord' => 'Create record',
        'setVariables' => 'Set variables', 'applyTransition' => 'Move lifecycle', 'notify' => 'Send notification',
        'webhook' => 'Call webhook', 'action' => 'Run action', 'subWorkflow' => 'Run workflow', 'goTo' => 'Go back',
        'end' => 'End',
    ];

    /** The outputs each step type makes available as `steps.ID.*`. */
    public const array STEP_OUTPUTS = [
        'branch' => ['path'], 'switch' => ['case'], 'parallel' => ['completed'], 'forEach' => ['count'],
        'approval' => ['outcome', 'decidedBy', 'comments', 'data'], 'task' => ['outcome', 'decidedBy', 'comment', 'data'],
        'wait' => ['timedOut', 'data'], 'setFields' => [], 'createRecord' => ['id'], 'setVariables' => [], 'applyTransition' => ['applied'],
        'notify' => ['sent', 'error'], 'webhook' => ['status', 'body', 'error'], 'action' => ['error'],
        'subWorkflow' => ['instanceId', 'status', 'outputs', 'error'], 'goTo' => ['count'], 'end' => [],
    ];

    /** @return list<LintProblem> every problem found (empty when the definition is fine) */
    public static function lint(WorkflowDefinition $definition, LintContext $context): array
    {
        return (new LintRun($definition, $context))->go();
    }
}
