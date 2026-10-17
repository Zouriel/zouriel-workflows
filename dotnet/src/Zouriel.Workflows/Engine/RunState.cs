using System.Text.Json;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Engine;

internal static class TokenStatus
{
    public const string Active = "active";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
}

/// <summary>A position in a step list.</summary>
internal sealed class Frame
{
    public string List { get; set; } = "root";
    public int Index { get; set; }
    /// <summary>Retry attempt of the step at <see cref="Index"/> (1-based).</summary>
    public int Attempt { get; set; } = 1;
    /// <summary>Inside <c>forEach</c>: the current item's values and position.</summary>
    public Dictionary<string, object?>? Item { get; set; }
    public int? ItemIndex { get; set; }
}

/// <summary>A thread of execution. Parallel paths and parallel forEach items run in child tokens.</summary>
internal sealed class Token
{
    public string Id { get; set; } = "root";
    public string? ParentId { get; set; }
    /// <summary>The join (parallel/forEach step) this child token belongs to.</summary>
    public string? JoinKey { get; set; }
    /// <summary>The path id or item index this child token runs.</summary>
    public string? Branch { get; set; }
    public List<Frame> Frames { get; set; } = [];
    public string Status { get; set; } = TokenStatus.Active;
    /// <summary>What a waiting token waits for: <c>task:ID</c>, <c>timer</c>, <c>signal</c>, <c>condition</c>, <c>join</c>, <c>child:ID</c>, <c>retry</c>, <c>incident</c>.</summary>
    public string? Waiting { get; set; }
    /// <summary>The step record of the step this token is waiting in (approval, wait, retry…).</summary>
    public string? StepRecord { get; set; }
    /// <summary>While waiting in an incident: the error, and the step that failed (null when a loop guard stopped it).</summary>
    public string? IncidentError { get; set; }
    public string? IncidentStepId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public Frame Top => Frames[^1];

    /// <summary>The innermost forEach item visible to this token.</summary>
    public (Dictionary<string, object?>? Item, int? Index) CurrentItem()
    {
        for (var i = Frames.Count - 1; i >= 0; i--)
            if (Frames[i].Item is not null) return (Frames[i].Item, Frames[i].ItemIndex);
        return (null, null);
    }
}

internal sealed class JoinState
{
    public string StepId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string Kind { get; set; } = "parallel";
    public List<string> Children { get; set; } = [];
    public List<string> Done { get; set; } = [];
    /// <summary>Parallel: paths needed before continuing.</summary>
    public int Required { get; set; }
    /// <summary>forEach: all items, and the indexes not yet started.</summary>
    public List<Dictionary<string, object?>>? Items { get; set; }
    public List<int>? Pending { get; set; }
    public int Concurrency { get; set; } = 5;
}

internal sealed class LoopState
{
    public List<Dictionary<string, object?>> Items { get; set; } = [];
}

/// <summary>Everything an instance needs to continue later, persisted as JSON in <c>InstanceRecord.Data</c>.</summary>
internal sealed class RunState
{
    public string RootList { get; set; } = "root";
    public List<Token> Tokens { get; set; } = [];
    public Dictionary<string, Dictionary<string, object?>> Outputs { get; set; } = [];
    public Dictionary<string, object?> Vars { get; set; } = [];
    public Dictionary<string, object?> Input { get; set; } = [];
    /// <summary>For runs about a deleted record: its values just before deletion (what <c>entity.*</c> reads).</summary>
    public Dictionary<string, object?>? Snapshot { get; set; }
    public Dictionary<string, int> GoTo { get; set; } = [];
    public Dictionary<string, JoinState> Joins { get; set; } = [];
    public Dictionary<string, LoopState> Loops { get; set; } = [];
    public int NextToken { get; set; } = 1;

    public Token? Token(string id) => Tokens.FirstOrDefault(t => t.Id == id);

    public string NewTokenId() => $"t{NextToken++}";

    public void SetOutput(string stepId, string key, object? value)
    {
        if (!Outputs.TryGetValue(stepId, out var o)) Outputs[stepId] = o = [];
        o[key] = Values.Normalize(value);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Definitions.JsonOptions);

    public static RunState FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new RunState();
        var s = JsonSerializer.Deserialize<RunState>(json, Definitions.JsonOptions) ?? new RunState();
        // object-typed values come back as JsonElement; turn them into workflow values again.
        foreach (var k in s.Outputs.Keys.ToList()) s.Outputs[k] = NormalizeMap(s.Outputs[k]);
        s.Vars = NormalizeMap(s.Vars);
        s.Input = NormalizeMap(s.Input);
        if (s.Snapshot is not null) s.Snapshot = NormalizeMap(s.Snapshot);
        foreach (var t in s.Tokens)
        foreach (var f in t.Frames)
            if (f.Item is not null) f.Item = NormalizeMap(f.Item);
        foreach (var j in s.Joins.Values)
            if (j.Items is not null) j.Items = j.Items.Select(NormalizeMap).ToList();
        foreach (var l in s.Loops.Values) l.Items = l.Items.Select(NormalizeMap).ToList();
        return s;
    }

    private static Dictionary<string, object?> NormalizeMap(Dictionary<string, object?> m) =>
        m.ToDictionary(kv => kv.Key, kv => Values.Normalize(kv.Value), StringComparer.Ordinal);
}

/// <summary>What started an instance (spec §3 <c>trigger.*</c>).</summary>
internal sealed class TriggerInfo
{
    public string Type { get; set; } = "";
    public string? Field { get; set; }
    public object? From { get; set; }
    public object? To { get; set; }
    public string? Transition { get; set; }
    public string? ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? At { get; set; }
    public string? EventId { get; set; }
    public Dictionary<string, object?>? Data { get; set; }

    public string ToJson() => Values.Serialize(ToValue());

    public Dictionary<string, object?> ToValue() => new()
    {
        ["type"] = Type,
        ["field"] = Field,
        ["from"] = Values.Normalize(From),
        ["to"] = Values.Normalize(To),
        ["transition"] = Transition,
        ["actor"] = ActorId,
        ["actorType"] = ActorType,
        ["actorName"] = ActorName,
        ["at"] = At,
        ["eventId"] = EventId,
        ["data"] = Data is null ? null : Values.Normalize(Data),
    };

    public static TriggerInfo FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TriggerInfo();
        var v = Values.FromJson(json) as Dictionary<string, object?> ?? [];
        string? S(string k) => v.TryGetValue(k, out var x) ? x as string : null;
        return new TriggerInfo
        {
            Type = S("type") ?? "",
            Field = S("field"),
            From = v.GetValueOrDefault("from"),
            To = v.GetValueOrDefault("to"),
            Transition = S("transition"),
            ActorId = S("actor"),
            ActorType = S("actorType"),
            ActorName = S("actorName"),
            At = S("at"),
            EventId = S("eventId"),
            Data = v.GetValueOrDefault("data") as Dictionary<string, object?>,
        };
    }
}
