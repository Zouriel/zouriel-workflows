<?php

namespace Zouriel\Workflows\Laravel\Http;

use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Host\WorkflowActor;

/**
 * Record-level access for the workflow API: which records, instances and tasks a person may see. The API asks it on
 * every read of a record, instance or task (and before transitions, resets, manual starts, interventions and test runs
 * on a record), on top of the route gates. Extend {@see RecordAccessPolicy} to get the default rules for instances and
 * tasks. Port of .NET `IWorkflowAccessPolicy`.
 */
interface AccessPolicy
{
    /** May `$actor` see this record (and act on it through the workflow API)? */
    public function canViewRecord(WorkflowActor $actor, string $entity, string $id): bool;

    /** May `$actor` see this instance? */
    public function canViewInstance(WorkflowActor $actor, InstanceView $instance): bool;

    /** May `$actor` see this task? */
    public function canViewTask(WorkflowActor $actor, TaskView $task): bool;
}
