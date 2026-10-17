using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zouriel.Workflows.Model;

/// <summary>Base of every step (spec §7). The concrete type is chosen by <c>type</c>.</summary>
[JsonConverter(typeof(StepConverter))]
public abstract class Step
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public string? Note { get; set; }
    public bool? Disabled { get; set; }
    public RetryPolicy? Retry { get; set; }
    public string? Timeout { get; set; }
    [JsonConverter(typeof(OnErrorConverter))] public OnError? OnError { get; set; }

    /// <summary>The step lists nested inside this step, with their slot names (spec "list ids").</summary>
    public virtual IEnumerable<(string Slot, List<Step> Steps)> Children() => [];
}

public sealed class RetryPolicy
{
    public int Max { get; set; }
    public string? Backoff { get; set; }
    public string? Initial { get; set; }
    public string? MaxDelay { get; set; }
}

/// <summary><c>"stop"</c>, <c>"continue"</c> or <c>{ steps }</c>.</summary>
public sealed class OnError
{
    public string Mode { get; init; } = "stop";
    public List<Step>? Steps { get; init; }
}

public sealed class StepList
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public List<Step> Steps { get; set; } = [];
}

public sealed class BranchPath
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public Condition? When { get; set; }
    public List<Step> Steps { get; set; } = [];
}

public sealed class BranchStep : Step
{
    public List<BranchPath> Paths { get; set; } = [];
    public StepList? Otherwise { get; set; }

    public override IEnumerable<(string, List<Step>)> Children()
    {
        foreach (var p in Paths) yield return ($"path:{p.Id}", p.Steps);
        if (Otherwise is not null) yield return ("otherwise", Otherwise.Steps);
    }
}

public sealed class SwitchCase
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<JsonNode?> Values { get; set; } = [];
    public List<Step> Steps { get; set; } = [];
}

public sealed class SwitchStep : Step
{
    public string On { get; set; } = "";
    public List<SwitchCase> Cases { get; set; } = [];
    public StepList? Otherwise { get; set; }

    public override IEnumerable<(string, List<Step>)> Children()
    {
        foreach (var c in Cases) yield return ($"case:{c.Id}", c.Steps);
        if (Otherwise is not null) yield return ("otherwise", Otherwise.Steps);
    }
}

public sealed class ParallelJoin
{
    public string Type { get; set; } = "all";
    public int? Count { get; set; }
}

public sealed class ParallelStep : Step
{
    public List<StepList> Paths { get; set; } = [];
    public ParallelJoin Join { get; set; } = new();

    public override IEnumerable<(string, List<Step>)> Children() => Paths.Select(p => ($"path:{p.Id}", p.Steps));
}

public sealed class ForEachStep : Step
{
    public string Collection { get; set; } = "";
    public Condition? When { get; set; }
    public string? Mode { get; set; }
    public int? Concurrency { get; set; }
    public int Max { get; set; } = 100;
    public List<Step> Steps { get; set; } = [];

    [JsonIgnore] public bool IsParallel => Mode == "parallel";

    public override IEnumerable<(string, List<Step>)> Children() => [("each", Steps)];
}

public sealed class Escalation
{
    public string After { get; set; } = "";
    public List<Assignee> Assignees { get; set; } = [];
    public bool? Notify { get; set; }
}

public sealed class ApprovalRule
{
    public string Type { get; set; } = "any";
    public int? Value { get; set; }
}

public sealed class ApprovalOutcomes
{
    public List<Step> Approved { get; set; } = [];
    public List<Step> Rejected { get; set; } = [];
    public List<Step> TimedOut { get; set; } = [];
}

/// <summary>Shared by <see cref="ApprovalStep"/> and <see cref="TaskStep"/>.</summary>
public abstract class HumanStep : Step
{
    public List<Assignee> Assignees { get; set; } = [];
    public string Due { get; set; } = "";
    public string? Calendar { get; set; }
    public List<string>? Reminders { get; set; }
    public Escalation? Escalation { get; set; }
    public JsonObject? Form { get; set; }
    public Template? Instructions { get; set; }
    public bool? AllowDelegation { get; set; }
    public string? RequireComment { get; set; }
}

public sealed class ApprovalStep : HumanStep
{
    public ApprovalRule Rule { get; set; } = new();
    public ApprovalOutcomes Outcomes { get; set; } = new();

    public override IEnumerable<(string, List<Step>)> Children() =>
        [("outcome:approved", Outcomes.Approved), ("outcome:rejected", Outcomes.Rejected), ("outcome:timedOut", Outcomes.TimedOut)];
}

public sealed class TaskOutcome
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Tone { get; set; }
    public List<Step> Steps { get; set; } = [];
}

public sealed class TaskStep : HumanStep
{
    public List<TaskOutcome> Outcomes { get; set; } = [];
    public StepList OnTimeout { get; set; } = new();

    public override IEnumerable<(string, List<Step>)> Children()
    {
        foreach (var o in Outcomes) yield return ($"outcome:{o.Key}", o.Steps);
        yield return ("onTimeout", OnTimeout.Steps);
    }
}

public sealed class WaitUntil
{
    public string Type { get; set; } = "duration";
    public string? Duration { get; set; }
    public string? Calendar { get; set; }
    public Operand? Date { get; set; }
    public string? Offset { get; set; }
    public string? At { get; set; }
    public Condition? When { get; set; }
    public string? Timeout { get; set; }
    public StepList? OnTimeout { get; set; }
    public string? Signal { get; set; }
}

public sealed class WaitStep : Step
{
    public WaitUntil Until { get; set; } = new();

    public override IEnumerable<(string, List<Step>)> Children() =>
        Until.OnTimeout is null ? [] : [("onTimeout", Until.OnTimeout.Steps)];
}

public sealed class FieldValue
{
    public string Field { get; set; } = "";
    public Operand Value { get; set; } = Operand.Of(null);
}

public sealed class SetFieldsStep : Step
{
    public string? Target { get; set; }
    public List<FieldValue> Values { get; set; } = [];
}

public sealed class VariableValue
{
    public string Variable { get; set; } = "";
    public Operand Value { get; set; } = Operand.Of(null);
}

public sealed class SetVariablesStep : Step
{
    public List<VariableValue> Values { get; set; } = [];
}

public sealed class ApplyTransitionStep : Step
{
    public string? Target { get; set; }
    public string Lifecycle { get; set; } = "";
    public string Transition { get; set; } = "";
    public Dictionary<string, Operand>? Input { get; set; }
    public string? IfNotAllowed { get; set; }
}

public sealed class NotifyStep : Step
{
    public string Channel { get; set; } = "";
    public List<Assignee> To { get; set; } = [];
    public Template? Subject { get; set; }
    public Template Body { get; set; } = new();
    [JsonConverter(typeof(TemplateConverter))] public Template? Link { get; set; }
}

public sealed class WebhookStep : Step
{
    public string? Connection { get; set; }
    public string? Url { get; set; }
    public string Method { get; set; } = "POST";
    public Template? Path { get; set; }
    public Dictionary<string, Template>? Headers { get; set; }
    public JsonNode? Body { get; set; }
    public List<int>? Expect { get; set; }
}

public sealed class ActionStep : Step
{
    public string Action { get; set; } = "";
    public Dictionary<string, Operand>? Input { get; set; }
}

public sealed class SubWorkflowStep : Step
{
    public string Workflow { get; set; } = "";
    public string? Target { get; set; }
    public bool Wait { get; set; }
    public Dictionary<string, Operand>? Input { get; set; }
}

public sealed class GoToStep : Step
{
    public string Target { get; set; } = "";
    public int MaxTimes { get; set; } = 1;
}

public sealed class EndStep : Step
{
    public string Result { get; set; } = "completed";
    public Template? Reason { get; set; }
}

/// <summary>A step type this engine does not know (from a newer spec). Lint reports it; running it fails.</summary>
public sealed class UnknownStep : Step;

public static class StepTypes
{
    public static readonly IReadOnlyDictionary<string, Type> All = new Dictionary<string, Type>
    {
        ["branch"] = typeof(BranchStep),
        ["switch"] = typeof(SwitchStep),
        ["parallel"] = typeof(ParallelStep),
        ["forEach"] = typeof(ForEachStep),
        ["approval"] = typeof(ApprovalStep),
        ["task"] = typeof(TaskStep),
        ["wait"] = typeof(WaitStep),
        ["setFields"] = typeof(SetFieldsStep),
        ["setVariables"] = typeof(SetVariablesStep),
        ["applyTransition"] = typeof(ApplyTransitionStep),
        ["notify"] = typeof(NotifyStep),
        ["webhook"] = typeof(WebhookStep),
        ["action"] = typeof(ActionStep),
        ["subWorkflow"] = typeof(SubWorkflowStep),
        ["goTo"] = typeof(GoToStep),
        ["end"] = typeof(EndStep),
    };
}
