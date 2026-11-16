<?php

namespace Zouriel\Workflows\Laravel\Http;

use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Host\WorkflowActor;

/**
 * An access policy where instances and tasks follow the record they are about (the defaults of .NET's
 * `IWorkflowAccessPolicy`): instances about no record are visible, and assignees always see their tasks.
 */
abstract class RecordAccessPolicy implements AccessPolicy
{
    public function canViewInstance(WorkflowActor $actor, InstanceView $instance): bool
    {
        return $instance->entity === null || $instance->entityId === null || $this->canViewRecord($actor, $instance->entity, $instance->entityId);
    }

    public function canViewTask(WorkflowActor $actor, TaskView $task): bool
    {
        if ($actor->id !== null && in_array($actor->id, $task->assignees, true)) {
            return true;
        }

        return $task->entity === null || $task->entityId === null || $this->canViewRecord($actor, $task->entity, $task->entityId);
    }
}
