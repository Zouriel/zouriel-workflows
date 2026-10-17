using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Engine;

/// <summary>
/// Resolves token paths (spec §3) for one evaluation. Record values are read fresh from the gateway;
/// relation hops load the related record. Call <c>PrefetchAsync</c> with the paths a
/// condition, operand or template reads, then evaluate synchronously.
/// </summary>
public sealed class ValueScope : IValueContext
{
    private readonly IEntityGateway _gateway;
    private readonly WorkflowRegistry _registry;
    private readonly Dictionary<string, object?> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), RecordSnapshot?> _records = [];

    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    /// <summary>Values for a deleted record (the snapshot taken before deletion).</summary>
    public Dictionary<string, object?>? EntityOverride { get; init; }
    public TriggerInfo? Trigger { get; init; }
    public RunState? State { get; init; }
    public Dictionary<string, object?>? Item { get; init; }
    public int? ItemIndex { get; init; }
    public string? ItemEntity { get; init; }
    public Dictionary<string, object?>? Input { get; init; }
    public InstanceRecord? Instance { get; init; }
    public Func<string, string>? VariableKey { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required string Today { get; init; }

    public ValueScope(IEntityGateway gateway, WorkflowRegistry registry)
    {
        _gateway = gateway;
        _registry = registry;
    }

    public async Task PrefetchAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths.Distinct())
            if (!_resolved.ContainsKey(p)) _resolved[p] = await ResolveAsync(p, ct);
    }

    public Task PrefetchAsync(Condition? c, CancellationToken ct = default) => PrefetchAsync(Conditions.Refs(c), ct);

    public object? Read(string path) => _resolved.TryGetValue(path, out var v) ? v : null;

    public async Task<object?> ResolveAsync(string path, CancellationToken ct = default)
    {
        if (_resolved.TryGetValue(path, out var cached)) return cached;
        var parts = path.Split('.');
        object? value = parts[0] switch
        {
            "entity" => await ReadRecordPathAsync(Entity, EntityId, EntityOverride, parts.AsSpan(1).ToArray(), ct),
            "item" => parts.Length == 2 && parts[1] == "index"
                ? ItemIndex
                : await ReadRecordPathAsync(ItemEntity, Item?.GetValueOrDefault("id") as string, Item, parts.AsSpan(1).ToArray(), ct),
            "trigger" => Values.ReadPath(Trigger?.ToValue(), parts.Skip(1)),
            "input" => Values.ReadPath(Input ?? State?.Input, parts.Skip(1)),
            "steps" => parts.Length >= 3 && State?.Outputs.TryGetValue(parts[1], out var o) == true ? Values.ReadPath(o, parts.Skip(2)) : null,
            "vars" => parts.Length >= 2 ? Values.ReadPath(State?.Vars.GetValueOrDefault(parts[1]), parts.Skip(2)) : null,
            "instance" => parts.ElementAtOrDefault(1) switch
            {
                "id" => Instance?.Id,
                "startedAt" => Instance is null ? null : Values.FormatInstant(Instance.StartedAt),
                "initiator" => Instance?.InitiatorId,
                _ => null,
            },
            "now" => Values.FormatInstant(Now),
            "today" => Today,
            _ => null,
        };
        value = Values.Normalize(value);
        _resolved[path] = value;
        return value;
    }

    private async Task<object?> ReadRecordPathAsync(string? entity, string? id, Dictionary<string, object?>? preset, string[] rest, CancellationToken ct)
    {
        if (entity is null || rest.Length == 0) return null;
        Dictionary<string, object?>? values = preset;
        if (values is null)
        {
            if (id is null) return null;
            values = (await LoadAsync(entity, id, ct))?.Values;
        }
        if (values is null) return null;
        var key = rest[0];
        if (key == "id") return id ?? values.GetValueOrDefault("id");
        var field = _registry.Field(entity, key);
        if (field is null || !field.IsReadable) return null;
        var v = values.GetValueOrDefault(key);
        if (rest.Length == 1) return v;
        if (field.Type == FieldTypes.Relation && v is not null && field.Entity is not null)
            return await ReadRecordPathAsync(field.Entity, Values.Text(v), null, rest[1..], ct);
        return Values.ReadPath(v, rest.Skip(1));
    }

    public async Task<RecordSnapshot?> LoadAsync(string entity, string id, CancellationToken ct)
    {
        if (_records.TryGetValue((entity, id), out var r)) return r;
        r = await _gateway.GetAsync(entity, id, ct);
        _records[(entity, id)] = r;
        return r;
    }

    /// <summary>The registered field behind an <c>entity.*</c> or <c>item.*</c> path, for formatting.</summary>
    public FieldDescriptor? FieldOf(string path)
    {
        var parts = path.Split('.');
        string? entity = parts[0] switch { "entity" => Entity, "item" => ItemEntity, _ => null };
        if (entity is null) return null;
        FieldDescriptor? f = null;
        for (var i = 1; i < parts.Length; i++)
        {
            f = _registry.Field(entity, parts[i]);
            if (f is null) return null;
            if (f.Type == FieldTypes.Relation && i < parts.Length - 1) entity = f.Entity;
            else if (i < parts.Length - 1) return null;
        }
        return f;
    }

    /// <summary>Forgets cached record values (after the workflow wrote to the record).</summary>
    public void Invalidate()
    {
        _resolved.Clear();
        _records.Clear();
    }

    public async Task<object?> OperandAsync(Operand? o, CancellationToken ct = default)
    {
        if (o is null) return null;
        if (o.Ref is not null) return await ResolveAsync(o.Ref, ct);
        if (o.Calc is not null) await PrefetchAsync(CalcRefs(o.Calc), ct);
        return Conditions.ResolveOperand(o, this);
    }

    public async Task<bool> ConditionAsync(Condition? c, CancellationToken ct = default)
    {
        await PrefetchAsync(c, ct);
        return Conditions.Evaluate(c, this);
    }

    public static IEnumerable<string> CalcRefs(Calc c) =>
        c.Args.SelectMany(a => a.Ref is not null ? [a.Ref] : a.Calc is not null ? CalcRefs(a.Calc) : []);
}
