using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Changes;

/// <summary>What the change hook should do with one record write.</summary>
public sealed class InspectResult
{
    /// <summary>The event to write to the outbox in the same transaction (null when nothing workflows care about changed).</summary>
    public EntityChange? Change { get; init; }
    /// <summary>Values to set before saving: the initial state of lifecycle fields left empty on new records.</summary>
    public Dictionary<string, object?> InitialValues { get; init; } = [];
}

/// <summary>
/// Turns a record write into a change event (spec §5) and enforces lifecycle field ownership (spec §6).
/// Called by gateways and the EF Core interceptor before the write commits.
/// </summary>
public sealed class ChangeInspector
{
    private readonly WorkflowEngine _engine;

    internal ChangeInspector(WorkflowEngine engine) => _engine = engine;

    /// <param name="before">Registered field values before the write (null for creates).</param>
    /// <param name="after">Registered field values after the write (null for deletes).</param>
    public InspectResult Inspect(string entity, string id, string kind, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after)
    {
        var desc = _engine.Registry.Entity(entity);
        if (desc is null) return new InspectResult();
        var scope = WorkflowWriteScope.Current;
        var owned = _engine.Ownership.ForEntity(entity).ToList();
        var initial = new Dictionary<string, object?>();
        var fields = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        var engineFields = new List<string>();

        foreach (var f in desc.Fields)
        {
            if (!FieldTypes.IsScalar(f.Type) && f.Type != FieldTypes.Relation) continue;
            var from = before is null ? null : Values.Normalize(before.GetValueOrDefault(f.Key));
            var to = after is null ? null : Values.Normalize(after.GetValueOrDefault(f.Key));
            var lc = owned.FirstOrDefault(o => o.Field == f.Key);
            if (kind == ChangeKinds.Created && lc is not null && Values.IsEmpty(to) && lc.InitialState is not null)
            {
                initial[f.Key] = lc.InitialState;
                to = lc.InitialState;
                engineFields.Add(f.Key);
            }
            if (kind == ChangeKinds.Updated && Values.DeepEquals(from, to)) continue;
            if (kind != ChangeKinds.Deleted) fields[f.Key] = new FieldChange { From = from, To = to };
            if (kind == ChangeKinds.Updated && lc is not null)
            {
                if (scope?.MayWriteOwned(entity, id, f.Key) == true) engineFields.Add(f.Key);
                else if (lc.Enforce)
                {
                    _engine.RecordRefusedWrite(entity, id, f.Key, lc.DefinitionId, from, to, scope);
                    throw new WorkflowFieldOwnedException(entity, f.Key, lc.DefinitionKey);
                }
            }
        }
        if (kind == ChangeKinds.Updated && fields.Count == 0) return new InspectResult { InitialValues = initial };
        var change = new EntityChange
        {
            Kind = kind, Entity = entity, EntityId = id, Fields = kind == ChangeKinds.Deleted ? [] : fields,
            Snapshot = kind == ChangeKinds.Deleted && before is not null ? before.ToDictionary(kv => kv.Key, kv => Values.Normalize(kv.Value)) : null,
            ActorType = scope?.Actor.Type ?? _engine.CurrentActor()?.Type ?? "system",
            ActorId = scope?.Actor.Id ?? _engine.CurrentActor()?.Id,
            ActorName = scope?.Actor.Name ?? _engine.CurrentActor()?.Name,
            Depth = scope?.Depth ?? 0, CausationInstanceId = scope?.InstanceId, CausationDefinitionId = scope?.DefinitionId,
            EngineFields = engineFields.Count > 0 ? engineFields : null, CorrelationId = scope?.CorrelationId, At = _engine.Options.Time.GetUtcNow(),
        };
        return new InspectResult { Change = change, InitialValues = initial };
    }

    /// <summary>The outbox row for a change event.</summary>
    public OutboxRecord ToOutbox(EntityChange change) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = OutboxKinds.Change, Payload = change.ToJson(), CreatedAt = change.At,
    };
}

/// <summary>Supplies the person behind record writes made outside workflows (the logged-in user), for change events and audit.</summary>
public interface IWorkflowActorAccessor
{
    /// <summary>The person behind the current request, or null outside one (writes are then attributed to the system).</summary>
    WorkflowActor? Current { get; }
}
