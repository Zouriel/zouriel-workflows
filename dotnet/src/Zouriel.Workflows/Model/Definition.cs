using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zouriel.Workflows.Model;

/// <summary>A workflow definition (spec §1–§7). Parsed from the JSON the builder writes.</summary>
public sealed class WorkflowDefinition
{
    public int SpecVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Kind { get; set; } = WorkflowKinds.Automation;
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int? Revision { get; set; }
    public string? Entity { get; set; }
    public Trigger? Trigger { get; set; }
    public List<Step>? Steps { get; set; }
    public Lifecycle? Lifecycle { get; set; }
    public List<Variable>? Variables { get; set; }
    public WorkflowSettings? Settings { get; set; }

    [JsonIgnore] public bool IsLifecycle => Kind == WorkflowKinds.Lifecycle;
}

public static class WorkflowKinds
{
    public const string Automation = "automation";
    public const string Lifecycle = "lifecycle";
}

public sealed class Variable
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public JsonNode? Default { get; set; }
}

public sealed class WorkflowSettings
{
    public string? Concurrency { get; set; }
    public List<Assignee>? Owners { get; set; }
    public bool? NotifyOwnersOnIncident { get; set; }
    public string? Calendar { get; set; }
}

public static class TriggerTypes
{
    public const string Created = "created";
    public const string FieldChanged = "fieldChanged";
    public const string StateEntered = "stateEntered";
    public const string TransitionApplied = "transitionApplied";
    public const string Deleted = "deleted";
    public const string DateReached = "dateReached";
    public const string Schedule = "schedule";
    public const string Manual = "manual";
    public const string Signal = "signal";
}

public sealed class Trigger
{
    public string Type { get; set; } = "";
    public Condition? When { get; set; }
    public bool? Retrigger { get; set; }
    public string? Field { get; set; }
    public List<JsonNode?>? From { get; set; }
    public List<JsonNode?>? To { get; set; }
    public string? Lifecycle { get; set; }
    public List<string>? States { get; set; }
    public List<string>? Transitions { get; set; }
    public string? Offset { get; set; }
    public string? At { get; set; }
    public string? Cron { get; set; }
    public ScheduleForEach? ForEach { get; set; }
    public string? Label { get; set; }
    public JsonObject? Form { get; set; }
    public List<Assignee>? Who { get; set; }
    public string? Signal { get; set; }
}

public sealed class ScheduleForEach
{
    public Condition? When { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class Lifecycle
{
    public string Field { get; set; } = "";
    public string? Mode { get; set; }
    public List<State> States { get; set; } = [];
    public List<Transition> Transitions { get; set; } = [];
    public Dictionary<string, double[]>? Layout { get; set; }

    [JsonIgnore] public bool Enforce => Mode != "observe";
}

public sealed class State
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Category { get; set; } = "active";
    public string? Tone { get; set; }
    public string? Description { get; set; }
    public List<Step>? OnEnter { get; set; }
    public List<Step>? OnExit { get; set; }
    public bool? KeepRunsOnExit { get; set; }
    public Sla? Sla { get; set; }

    [JsonIgnore] public bool IsTerminal => Category == "terminal";
    [JsonIgnore] public bool IsInitial => Category == "initial";
}

public sealed class Sla
{
    public string Within { get; set; } = "";
    public string? Calendar { get; set; }
    public List<Step> Steps { get; set; } = [];
}

public sealed class Transition
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    [JsonConverter(typeof(TransitionFromConverter))] public TransitionFrom From { get; set; } = new();
    public string To { get; set; } = "";
    public List<Assignee>? Who { get; set; }
    public Condition? When { get; set; }
    public TransitionInput? Input { get; set; }
    public List<Step>? Steps { get; set; }
    public AutoTransition? Auto { get; set; }
    public string? Confirm { get; set; }
    public string? Tone { get; set; }
    public bool? Hidden { get; set; }
}

/// <summary><c>"*"</c> (any non-terminal state) or a list of state ids.</summary>
public sealed class TransitionFrom
{
    public bool Any { get; set; }
    public List<string> States { get; set; } = [];

    public bool Allows(Lifecycle lc, string stateId, string toId)
    {
        if (!Any) return States.Contains(stateId);
        var st = lc.States.FirstOrDefault(s => s.Id == stateId);
        return st is not null && !st.IsTerminal && stateId != toId;
    }
}

public sealed class TransitionInput
{
    public JsonObject Form { get; set; } = new();
}

public sealed class AutoTransition
{
    public string? After { get; set; }
    public string? Calendar { get; set; }
    public Condition? When { get; set; }
}

// ── Assignees ─────────────────────────────────────────────────────────────

/// <summary>One entry of an assignee list (spec §8). Exactly one property is set.</summary>
public sealed class Assignee
{
    public string? Role { get; set; }
    public string? Group { get; set; }
    public string? User { get; set; }
    public string? Field { get; set; }
    public string? ManagerOf { get; set; }
    public bool? Initiator { get; set; }
    /// <summary>Only for notification recipients: an address template.</summary>
    [JsonConverter(typeof(TemplateConverter))] public Template? Email { get; set; }
}

// ── Operands, templates, conditions ───────────────────────────────────────

/// <summary><c>{const}</c>, <c>{ref}</c>, <c>{today}</c> or <c>{calc}</c>.</summary>
[JsonConverter(typeof(OperandConverter))]
public sealed class Operand
{
    public bool HasConst { get; init; }
    public object? Const { get; init; }
    public string? Ref { get; init; }
    public int? Today { get; init; }
    public Calc? Calc { get; init; }

    public static Operand Of(object? value) => new() { HasConst = true, Const = Json.Values.Normalize(value) };
    public static Operand OfRef(string path) => new() { Ref = path };
}

public sealed class Calc
{
    public string Op { get; set; } = "sum";
    public List<CalcArg> Args { get; set; } = [];
    public int? Decimals { get; set; }
}

[JsonConverter(typeof(CalcArgConverter))]
public sealed class CalcArg
{
    public string? Ref { get; init; }
    public bool HasConst { get; init; }
    public object? Const { get; init; }
    public Calc? Calc { get; init; }
}

/// <summary>Text with token parts (spec §3).</summary>
[JsonConverter(typeof(TemplateConverter))]
public sealed class Template
{
    /// <summary>Each part is a <see cref="string"/> or a token path wrapped in <see cref="TemplateRef"/>.</summary>
    public List<object> Parts { get; init; } = [];

    public static Template Of(string text) => new() { Parts = [text] };
}

public sealed record TemplateRef(string Ref);

/// <summary>A condition rule or group, exactly the form builder's JSON (spec §4).</summary>
[JsonConverter(typeof(ConditionConverter))]
public abstract class Condition;

public sealed class ConditionGroup : Condition
{
    public string Match { get; set; } = "all";
    public List<Condition> Conditions { get; set; } = [];
}

public sealed class ConditionRule : Condition
{
    public string Ref { get; set; } = "";
    public string Op { get; set; } = "eq";
    public Operand? Value { get; set; }
    public Operand? Value2 { get; set; }
}
