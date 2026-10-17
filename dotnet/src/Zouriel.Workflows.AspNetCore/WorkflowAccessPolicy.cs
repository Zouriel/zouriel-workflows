using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.AspNetCore;

/// <summary>
/// Record-level access for the workflow API: which records, instances and tasks a person may see. The
/// API asks it on every read of a record, instance or task (and before transitions and manual starts on
/// a record), on top of the route policies. Register your own implementation to apply the app's rules;
/// by default instances and tasks follow the record they are about.
/// </summary>
public interface IWorkflowAccessPolicy
{
    /// <summary>May <paramref name="actor"/> see this record (and act on it through the workflow API)?</summary>
    ValueTask<bool> CanViewRecordAsync(WorkflowActor actor, string entity, string id, CancellationToken ct = default);

    /// <summary>May <paramref name="actor"/> see this instance? Default: when they may see its record (instances about no record are visible).</summary>
    ValueTask<bool> CanViewInstanceAsync(WorkflowActor actor, InstanceView instance, CancellationToken ct = default) =>
        instance.Entity is null || instance.EntityId is null ? ValueTask.FromResult(true) : CanViewRecordAsync(actor, instance.Entity, instance.EntityId, ct);

    /// <summary>May <paramref name="actor"/> see this task? Default: assignees always may; others when they may see its record.</summary>
    ValueTask<bool> CanViewTaskAsync(WorkflowActor actor, TaskView task, CancellationToken ct = default) =>
        actor.Id is not null && task.Assignees.Contains(actor.Id) ? ValueTask.FromResult(true)
        : task.Entity is null || task.EntityId is null ? ValueTask.FromResult(true) : CanViewRecordAsync(actor, task.Entity, task.EntityId, ct);
}

/// <summary>The default access policy: every signed-in caller may see every record (route policies still apply).</summary>
public sealed class AllowAllWorkflowAccessPolicy : IWorkflowAccessPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> CanViewRecordAsync(WorkflowActor actor, string entity, string id, CancellationToken ct = default) => ValueTask.FromResult(true);
}
