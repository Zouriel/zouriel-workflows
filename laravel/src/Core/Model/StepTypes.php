<?php

namespace Zouriel\Workflows\Core\Model;

/** The step types this engine runs, by their JSON `type`. Port of .NET `StepTypes`. */
final class StepTypes
{
    /** @var array<string, class-string<Step>> */
    public const array ALL = [
        'branch' => BranchStep::class,
        'switch' => SwitchStep::class,
        'parallel' => ParallelStep::class,
        'forEach' => ForEachStep::class,
        'approval' => ApprovalStep::class,
        'task' => TaskStep::class,
        'wait' => WaitStep::class,
        'setFields' => SetFieldsStep::class,
        'createRecord' => CreateRecordStep::class,
        'setVariables' => SetVariablesStep::class,
        'applyTransition' => ApplyTransitionStep::class,
        'notify' => NotifyStep::class,
        'webhook' => WebhookStep::class,
        'action' => ActionStep::class,
        'subWorkflow' => SubWorkflowStep::class,
        'goTo' => GoToStep::class,
        'end' => EndStep::class,
    ];
}
