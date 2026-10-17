using Zouriel.Workflows.Json;

namespace Zouriel.Workflows.Changes;

public static class ChangeKinds
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
}

/// <summary>One committed change to a registered record, carried through the outbox (spec §5).</summary>
public sealed class EntityChange
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = ChangeKinds.Updated;
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    /// <summary>Changed watchable fields: key → [from, to].</summary>
    public Dictionary<string, FieldChange> Fields { get; set; } = [];
    /// <summary>For deletes: the record's values just before deletion.</summary>
    public Dictionary<string, object?>? Snapshot { get; set; }
    public string? ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    /// <summary>0 for people and schedules; the writing instance's depth + 1 for workflow writes.</summary>
    public int Depth { get; set; }
    /// <summary>The instance whose step made the change, if any.</summary>
    public string? CausationInstanceId { get; set; }
    public string? CausationDefinitionId { get; set; }
    /// <summary>Fields written by the engine itself (lifecycle transitions); lifecycles skip these.</summary>
    public List<string>? EngineFields { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset At { get; set; }

    public WorkflowActor Actor => ActorType switch
    {
        "user" when ActorId is not null => WorkflowActor.User(ActorId, ActorName),
        "workflow" => new WorkflowActor("workflow", ActorId, ActorName),
        _ => WorkflowActor.System,
    };

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Model.Definitions.JsonOptions);

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

public sealed class FieldChange
{
    public object? From { get; set; }
    public object? To { get; set; }
}

/// <summary>A lifecycle event carried through the outbox to start <c>stateEntered</c>/<c>transitionApplied</c> automations.</summary>
public sealed class LifecycleEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string Lifecycle { get; set; } = "";
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? FromState { get; set; }
    public string ToState { get; set; } = "";
    public string? Transition { get; set; }
    public string? ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public int Depth { get; set; }
    /// <summary>The definition whose step applied the transition, if any (so it does not start itself again, spec §5).</summary>
    public string? CausationDefinitionId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset At { get; set; }

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

    public static WorkflowWriteScope? Current => CurrentScope.Value;

    public WorkflowActor Actor { get; }
    public int Depth { get; }
    public string? InstanceId { get; }
    public string? DefinitionId { get; }
    public string? CorrelationId { get; }
    /// <summary>"entity:id:field" entries the engine may write even when a lifecycle owns them.</summary>
    public HashSet<string> OwnedWrites { get; } = [];

    private WorkflowWriteScope(WorkflowActor actor, int depth, string? instanceId, string? definitionId, string? correlationId)
    {
        Actor = actor;
        Depth = depth;
        InstanceId = instanceId;
        DefinitionId = definitionId;
        CorrelationId = correlationId;
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    public static WorkflowWriteScope Begin(WorkflowActor actor, int depth, string? instanceId = null, string? definitionId = null, string? correlationId = null) =>
        new(actor, depth, instanceId, definitionId, correlationId);

    public static string OwnedKey(string entity, string id, string field) => $"{entity}:{id}:{field}";

    public void AllowOwned(string entity, string id, string field) => OwnedWrites.Add(OwnedKey(entity, id, field));

    public bool MayWriteOwned(string entity, string id, string field) => OwnedWrites.Contains(OwnedKey(entity, id, field));

    public void Dispose() => CurrentScope.Value = _previous;
}
