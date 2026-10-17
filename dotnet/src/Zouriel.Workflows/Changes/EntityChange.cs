using Zouriel.Workflows.Json;

namespace Zouriel.Workflows.Changes;

/// <summary>Kinds of record change (spec §5 triggers).</summary>
public static class ChangeKinds
{
    /// <summary>The record was created and committed.</summary>
    public const string Created = "created";
    /// <summary>Fields of the record changed.</summary>
    public const string Updated = "updated";
    /// <summary>The record was deleted.</summary>
    public const string Deleted = "deleted";
}

/// <summary>One committed change to a registered record, carried through the outbox (spec §5).</summary>
public sealed class EntityChange
{
    /// <summary>Unique id of this change; a trigger starts at most once per event (spec §5).</summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    /// <see cref="ChangeKinds"/>.
    public string Kind { get; set; } = ChangeKinds.Updated;
    /// <summary>The registry entity key.</summary>
    public string Entity { get; set; } = "";
    /// <summary>The record's id.</summary>
    public string EntityId { get; set; } = "";
    /// <summary>Changed watchable fields: key → [from, to].</summary>
    public Dictionary<string, FieldChange> Fields { get; set; } = [];
    /// <summary>For deletes: the record's values just before deletion.</summary>
    public Dictionary<string, object?>? Snapshot { get; set; }
    /// <summary>Who made the change: <c>user</c>, <c>workflow</c> or <c>system</c>.</summary>
    public string? ActorType { get; set; }
    /// <summary>Their id (user id or instance id).</summary>
    public string? ActorId { get; set; }
    /// <summary>Their display name.</summary>
    public string? ActorName { get; set; }
    /// <summary>0 for people and schedules; the writing instance's depth + 1 for workflow writes.</summary>
    public int Depth { get; set; }
    /// <summary>The instance whose step made the change, if any.</summary>
    public string? CausationInstanceId { get; set; }
    /// <summary>The definition whose step made the change, if any (it is not started again by its own write).</summary>
    public string? CausationDefinitionId { get; set; }
    /// <summary>Fields written by the engine itself (lifecycle transitions); lifecycles skip these.</summary>
    public List<string>? EngineFields { get; set; }
    /// <summary>The chain of work the change belongs to, if a workflow made it.</summary>
    public string? CorrelationId { get; set; }
    /// <summary>When the change was made (compared with a trigger's first publish).</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>
    /// True when the write must not start automations (a reset taking back what a workflow did, spec §6), so
    /// undoing a field cannot set off a workflow that redoes it. Lifecycle bookkeeping still follows the change.
    /// </summary>
    public bool SuppressTriggers { get; set; }

    /// <summary>The actor, rebuilt from the stored fields.</summary>
    public WorkflowActor Actor => ActorType switch
    {
        "user" when ActorId is not null => WorkflowActor.User(ActorId, ActorName),
        "workflow" => new WorkflowActor("workflow", ActorId, ActorName),
        _ => WorkflowActor.System,
    };

    /// <summary>The change as outbox JSON.</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Model.Definitions.JsonOptions);

    /// <summary>Reads a change from outbox JSON, with values turned back into workflow values.</summary>
    public static EntityChange FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<EntityChange>(json, Model.Definitions.JsonOptions)!.Normalized();

    private EntityChange Normalized()
    {
        foreach (var f in Fields.Values)
        {
            f.From = Values.Normalize(f.From);
            f.To = Values.Normalize(f.To);
        }
        if (Snapshot is not null) Snapshot = (Dictionary<string, object?>)Values.Normalize(Snapshot)!;
        return this;
    }
}

/// <summary>A field's value before and after a change.</summary>
public sealed class FieldChange
{
    /// <summary>The value before (null for creates).</summary>
    public object? From { get; set; }
    /// <summary>The value after.</summary>
    public object? To { get; set; }
}

/// <summary>A lifecycle event carried through the outbox to start <c>stateEntered</c>/<c>transitionApplied</c> automations.</summary>
public sealed class LifecycleEvent
{
    /// <summary>Unique id of this event (start dedupe).</summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The lifecycle definition key.</summary>
    public string Lifecycle { get; set; } = "";
    /// <summary>The record's entity.</summary>
    public string Entity { get; set; } = "";
    /// <summary>The record's id.</summary>
    public string EntityId { get; set; } = "";
    /// <summary>The state key left, or null when there was none (out of sync).</summary>
    public string? FromState { get; set; }
    /// <summary>The state key entered.</summary>
    public string ToState { get; set; } = "";
    /// <summary>The transition key applied, or null for a forced state.</summary>
    public string? Transition { get; set; }
    /// <summary>Who caused it: <c>user</c>, <c>workflow</c> or <c>system</c>.</summary>
    public string? ActorType { get; set; }
    /// <summary>Their id.</summary>
    public string? ActorId { get; set; }
    /// <summary>Their display name.</summary>
    public string? ActorName { get; set; }
    /// <summary>Loop protection depth of the change (spec §5).</summary>
    public int Depth { get; set; }
    /// <summary>The definition whose step applied the transition, if any (so it does not start itself again, spec §5).</summary>
    public string? CausationDefinitionId { get; set; }
    /// <summary>The record instance's correlation id.</summary>
    public string? CorrelationId { get; set; }
    /// <summary>When the transition happened.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The actor, rebuilt from the stored fields.</summary>
    public WorkflowActor Actor => ActorType == "user" && ActorId is not null ? WorkflowActor.User(ActorId, ActorName) : new WorkflowActor(ActorType ?? "system", ActorId, ActorName);
}

/// <summary>
/// Ambient context for code running inside the engine, so the change hook (EF interceptor) knows a
/// write came from a workflow: its depth for loop protection, and which owned fields it may write.
/// </summary>
public sealed class WorkflowWriteScope : IDisposable
{
    private static readonly AsyncLocal<WorkflowWriteScope?> CurrentScope = new();
    private readonly WorkflowWriteScope? _previous;

    /// <summary>The innermost scope on this async flow, or null outside workflow code.</summary>
    public static WorkflowWriteScope? Current => CurrentScope.Value;

    /// <summary>Who the writes are attributed to.</summary>
    public WorkflowActor Actor { get; }
    /// <summary>The loop protection depth changes made in this scope get.</summary>
    public int Depth { get; }
    /// <summary>The instance making the writes, if any.</summary>
    public string? InstanceId { get; }
    /// <summary>The definition making the writes (its own triggers ignore them unless they opt in).</summary>
    public string? DefinitionId { get; }
    /// <summary>The chain of work the writes belong to.</summary>
    public string? CorrelationId { get; }
    /// <summary>"entity:id:field" entries the engine may write even when a lifecycle owns them.</summary>
    public HashSet<string> OwnedWrites { get; } = [];
    /// <summary>Writes in this scope start no automations (a reset's undo, spec §6); carried on <see cref="EntityChange.SuppressTriggers"/>.</summary>
    public bool SuppressTriggers { get; }

    private WorkflowWriteScope(WorkflowActor actor, int depth, string? instanceId, string? definitionId, string? correlationId, bool suppressTriggers)
    {
        SuppressTriggers = suppressTriggers;
        Actor = actor;
        Depth = depth;
        InstanceId = instanceId;
        DefinitionId = definitionId;
        CorrelationId = correlationId;
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    /// <summary>Starts a scope for the current async flow; dispose it to restore the previous one.</summary>
    /// <param name="suppressTriggers">True when the writes must not start automations (a reset's undo). Nested scopes inherit it.</param>
    public static WorkflowWriteScope Begin(WorkflowActor actor, int depth, string? instanceId = null, string? definitionId = null, string? correlationId = null,
        bool suppressTriggers = false) =>
        new(actor, depth, instanceId, definitionId, correlationId, suppressTriggers || CurrentScope.Value?.SuppressTriggers == true);

    /// <summary>The <see cref="OwnedWrites"/> key for a record field.</summary>
    public static string OwnedKey(string entity, string id, string field) => $"{entity}:{id}:{field}";

    /// <summary>Lets this scope write a lifecycle-owned field of one record (the engine applying a transition).</summary>
    public void AllowOwned(string entity, string id, string field) => OwnedWrites.Add(OwnedKey(entity, id, field));

    /// <summary>Whether this scope may write a lifecycle-owned field of a record.</summary>
    public bool MayWriteOwned(string entity, string id, string field) => OwnedWrites.Contains(OwnedKey(entity, id, field));

    /// <summary>Ends the scope, restoring the one it replaced.</summary>
    public void Dispose() => CurrentScope.Value = _previous;
}
