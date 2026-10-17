using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zouriel.Workflows.Model;

/// <summary>Base of every step (spec §7). The concrete type is chosen by <c>type</c>.</summary>
[JsonConverter(typeof(StepConverter))]
public abstract class Step
{
    /// <summary>Immutable generated id; <c>steps.&lt;id&gt;.*</c> reads its outputs.</summary>
    public string Id { get; set; } = "";
    /// <summary>The step type (see <see cref="StepTypes"/>).</summary>
    public string Type { get; set; } = "";
    /// <summary>The name shown on the step card; null uses the step type's label.</summary>
    public string? Title { get; set; }
    /// <summary>A note for admins; never used at run time.</summary>
    public string? Note { get; set; }
    /// <summary>When true the step is skipped (audited as skipped).</summary>
    public bool? Disabled { get; set; }
    /// <summary>How a failed call-out step is retried before <see cref="OnError"/> applies; null uses the settings default.</summary>
    public RetryPolicy? Retry { get; set; }
    /// <summary>ISO duration after which an attempt fails (call-out steps).</summary>
    public string? Timeout { get; set; }
    /// <summary>What happens when the step fails after its retries; null = <c>stop</c>.</summary>
    [JsonConverter(typeof(OnErrorConverter))] public OnError? OnError { get; set; }

    /// <summary>The step lists nested inside this step, with their slot names (spec "list ids").</summary>
    public virtual IEnumerable<(string Slot, List<Step> Steps)> Children() => [];
}

/// <summary>Retry settings of a call-out step (spec §7).</summary>
public sealed class RetryPolicy
{
    /// <summary>Retries after the first attempt, 0 to 10.</summary>
    public int Max { get; set; }
    /// <c>fixed</c> (every delay is <see cref="Initial"/>) or <c>exponential</c> (doubling); null = fixed.
    public string? Backoff { get; set; }
    /// <summary>ISO duration of the first delay (default one minute).</summary>
    public string? Initial { get; set; }
    /// <summary>Cap on exponential delays; null = no cap.</summary>
    public string? MaxDelay { get; set; }
}

/// <summary><c>"stop"</c>, <c>"continue"</c> or <c>{ steps }</c>.</summary>
public sealed class OnError
{
    /// <c>stop</c> (incident), <c>continue</c> (carry on; output <c>error</c> holds the message) or <c>steps</c>.
    public string Mode { get; init; } = "stop";
    /// <summary>For <c>steps</c>: the steps run before continuing after the failed step.</summary>
    public List<Step>? Steps { get; init; }
}

/// <summary>A named list of steps (a parallel path, an <c>otherwise</c>, an <c>onTimeout</c>).</summary>
public sealed class StepList
{
    /// <summary>Immutable id of the list.</summary>
    public string Id { get; set; } = "";
    /// <summary>The name shown in the editor.</summary>
    public string? Label { get; set; }
    /// <summary>The steps, in order.</summary>
    public List<Step> Steps { get; set; } = [];
}

/// <summary>One path of a <c>branch</c> step: taken when its condition holds.</summary>
public sealed class BranchPath
{
    /// <summary>Immutable id; the branch's <c>path</c> output holds it.</summary>
    public string Id { get; set; } = "";
    /// <summary>The name shown in the editor.</summary>
    public string Label { get; set; } = "";
    /// <summary>The condition that selects this path.</summary>
    public Condition? When { get; set; }
    /// <summary>The steps of the path.</summary>
    public List<Step> Steps { get; set; } = [];
}

/// <summary>Runs the first path whose condition holds, else <c>otherwise</c> (output <c>path</c>).</summary>
public sealed class BranchStep : Step
{
    /// <summary>The paths, checked in order.</summary>
    public List<BranchPath> Paths { get; set; } = [];
    /// <summary>Runs when no path matches; null = nothing.</summary>
    public StepList? Otherwise { get; set; }

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children()
    {
        foreach (var p in Paths) yield return ($"path:{p.Id}", p.Steps);
        if (Otherwise is not null) yield return ("otherwise", Otherwise.Steps);
    }
}

/// <summary>One case of a <c>switch</c> step.</summary>
public sealed class SwitchCase
{
    /// <summary>Immutable id; the switch's <c>case</c> output holds it.</summary>
    public string Id { get; set; } = "";
    /// <summary>The name shown in the editor.</summary>
    public string Label { get; set; } = "";
    /// <summary>The values that select this case (matched like <c>anyOf</c>).</summary>
    public List<JsonNode?> Values { get; set; } = [];
    /// <summary>The steps of the case.</summary>
    public List<Step> Steps { get; set; } = [];
}

/// <summary>Runs the case whose values contain the value at <see cref="On"/> (output <c>case</c>).</summary>
public sealed class SwitchStep : Step
{
    /// <summary>The token path whose value picks the case.</summary>
    public string On { get; set; } = "";
    /// <summary>The cases, checked in order.</summary>
    public List<SwitchCase> Cases { get; set; } = [];
    /// <summary>Runs when no case matches; null = nothing.</summary>
    public StepList? Otherwise { get; set; }

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children()
    {
        foreach (var c in Cases) yield return ($"case:{c.Id}", c.Steps);
        if (Otherwise is not null) yield return ("otherwise", Otherwise.Steps);
    }
}

/// <summary>When a <c>parallel</c> step finishes.</summary>
public sealed class ParallelJoin
{
    /// <c>all</c> (every path), <c>any</c> (the first) or <c>count</c> (<see cref="Count"/> paths); the rest are cancelled.
    public string Type { get; set; } = "all";
    /// <summary>For <c>count</c>: how many paths must finish.</summary>
    public int? Count { get; set; }
}

/// <summary>Runs 2–10 paths at once and continues when the join is met (output <c>completed</c>).</summary>
public sealed class ParallelStep : Step
{
    /// <summary>The paths.</summary>
    public List<StepList> Paths { get; set; } = [];
    /// <summary>When the step finishes.</summary>
    public ParallelJoin Join { get; set; } = new();

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children() => Paths.Select(p => ($"path:{p.Id}", p.Steps));
}

/// <summary>Runs its steps once per item of a related collection (output <c>count</c>).</summary>
public sealed class ForEachStep : Step
{
    /// <summary>Token path of a collection field (e.g. <c>entity.documents</c>); items are read when the step starts.</summary>
    public string Collection { get; set; } = "";
    /// <summary>Only items matching this condition (on <c>item.*</c>) are used; null = all.</summary>
    public Condition? When { get; set; }
    /// <c>sequential</c> (default) or <c>parallel</c>.
    public string? Mode { get; set; }
    /// <summary>Parallel mode: items running at once (default 5).</summary>
    public int? Concurrency { get; set; }
    /// <summary>The most items used (1–500, default 100; settings may lower it).</summary>
    public int Max { get; set; } = 100;
    /// <summary>The steps run per item.</summary>
    public List<Step> Steps { get; set; } = [];

    /// <summary>True when <see cref="Mode"/> is <c>parallel</c>.</summary>
    [JsonIgnore] public bool IsParallel => Mode == "parallel";

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children() => [("each", Steps)];
}

/// <summary>Adds assignees to an open task after a time, without removing the original ones.</summary>
public sealed class Escalation
{
    /// <summary>ISO duration from the step's start.</summary>
    public string After { get; set; } = "";
    /// <summary>Who is added.</summary>
    public List<Assignee> Assignees { get; set; } = [];
    /// <summary>When false the added people are not notified; null = notified.</summary>
    public bool? Notify { get; set; }
}

/// <summary>How the decisions of an approval's assignees settle it (spec §7).</summary>
public sealed class ApprovalRule
{
    /// <c>any</c>, <c>all</c>, <c>count</c> or <c>percent</c>.
    public string Type { get; set; } = "any";
    /// <summary>For <c>count</c>: approvals needed; for <c>percent</c>: percent of assignees (rounded up).</summary>
    public int? Value { get; set; }
}

/// <summary>The steps run for each outcome of an approval.</summary>
public sealed class ApprovalOutcomes
{
    /// <summary>Run when approved.</summary>
    public List<Step> Approved { get; set; } = [];
    /// <summary>Run when rejected.</summary>
    public List<Step> Rejected { get; set; } = [];
    /// <summary>Run when the due time passes first.</summary>
    public List<Step> TimedOut { get; set; } = [];
}

/// <summary>Shared by <see cref="ApprovalStep"/> and <see cref="TaskStep"/>.</summary>
public abstract class HumanStep : Step
{
    /// <summary>Who decides; resolved when the step starts (none fails the step with <c>noAssignees</c>).</summary>
    public List<Assignee> Assignees { get; set; } = [];
    /// <summary>ISO duration until the task times out (required).</summary>
    public string Due { get; set; } = "";
    /// <c>working</c> to count working time only; null = calendar time.
    public string? Calendar { get; set; }
    /// <summary>ISO durations after which undecided assignees are reminded; null uses the settings default.</summary>
    public List<string>? Reminders { get; set; }
    /// <summary>Adds people when the task is still open after a time.</summary>
    public Escalation? Escalation { get; set; }
    /// <summary>A form-builder form each decision fills in (output <c>data</c>).</summary>
    public JsonObject? Form { get; set; }
    /// <summary>Text shown to the assignees.</summary>
    public Template? Instructions { get; set; }
    /// <summary>Whether assignees may hand the task to someone else; null uses the settings default.</summary>
    public bool? AllowDelegation { get; set; }
    /// <c>never</c>, <c>reject</c> (an approval's rejection, or a task outcome whose tone is <c>danger</c>, needs a comment) or <c>always</c>.
    public string? RequireComment { get; set; }
}

/// <summary>Asks people to approve or reject (outputs <c>outcome</c>, <c>decidedBy</c>, <c>comments</c>, <c>data</c>).</summary>
public sealed class ApprovalStep : HumanStep
{
    /// <summary>How decisions settle the approval.</summary>
    public ApprovalRule Rule { get; set; } = new();
    /// <summary>The steps per outcome.</summary>
    public ApprovalOutcomes Outcomes { get; set; } = new();

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children() =>
        [("outcome:approved", Outcomes.Approved), ("outcome:rejected", Outcomes.Rejected), ("outcome:timedOut", Outcomes.TimedOut)];
}

/// <summary>One button of a <c>task</c> step.</summary>
public sealed class TaskOutcome
{
    /// <summary>Immutable id.</summary>
    public string Id { get; set; } = "";
    /// <summary>The outcome key (the task's <c>outcome</c> output).</summary>
    public string Key { get; set; } = "";
    /// <summary>The button text.</summary>
    public string Label { get; set; } = "";
    /// <summary>Colour hint for the button.</summary>
    public string? Tone { get; set; }
    /// <summary>Run when this outcome is chosen.</summary>
    public List<Step> Steps { get; set; } = [];
}

/// <summary>Asks a person to choose one of up to eight outcomes; the first decision wins (outputs <c>outcome</c>, <c>decidedBy</c>, <c>comment</c>, <c>data</c>).</summary>
public sealed class TaskStep : HumanStep
{
    /// <summary>The buttons (1–8).</summary>
    public List<TaskOutcome> Outcomes { get; set; } = [];
    /// <summary>Run when the due time passes first.</summary>
    public StepList OnTimeout { get; set; } = new();

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children()
    {
        foreach (var o in Outcomes) yield return ($"outcome:{o.Key}", o.Steps);
        yield return ("onTimeout", OnTimeout.Steps);
    }
}

/// <summary>What a <c>wait</c> step waits for; which properties apply depends on <see cref="Type"/>.</summary>
public sealed class WaitUntil
{
    /// <c>duration</c>, <c>date</c>, <c>condition</c> or <c>signal</c>.
    public string Type { get; set; } = "duration";
    /// <c>duration</c>: how long, as an ISO duration.
    public string? Duration { get; set; }
    /// <c>duration</c>: <c>working</c> to count working time only.
    public string? Calendar { get; set; }
    /// <c>date</c>: the date or date-time to wait for.
    public Operand? Date { get; set; }
    /// <c>date</c>: signed ISO duration added to the date.
    public string? Offset { get; set; }
    /// <c>date</c> with a date value: time of day <c>HH:MM</c> (default 09:00).
    public string? At { get; set; }
    /// <c>condition</c>: waits until this holds (re-checked after each change to the record).
    public Condition? When { get; set; }
    /// <c>condition</c> (required) and <c>signal</c>: give up after this ISO duration.
    public string? Timeout { get; set; }
    /// <summary>Run when the wait times out; null = continue after the step.</summary>
    public StepList? OnTimeout { get; set; }
    /// <c>signal</c>: the signal key to wait for (about the same record).
    public string? Signal { get; set; }
}

/// <summary>Pauses the run (outputs <c>timedOut</c> and, for signals, <c>data</c>).</summary>
public sealed class WaitStep : Step
{
    /// <summary>What it waits for.</summary>
    public WaitUntil Until { get; set; } = new();

    /// <inheritdoc />
    public override IEnumerable<(string, List<Step>)> Children() =>
        Until.OnTimeout is null ? [] : [("onTimeout", Until.OnTimeout.Steps)];
}

/// <summary>One field a <c>setFields</c> step writes.</summary>
public sealed class FieldValue
{
    /// <summary>The settable field's key.</summary>
    public string Field { get; set; } = "";
    /// <summary>The new value.</summary>
    public Operand Value { get; set; } = Operand.Of(null);
}

/// <summary>Writes settable fields of the record (or a related one) in one committed change.</summary>
public sealed class SetFieldsStep : Step
{
    /// <c>self</c>/null for the workflow's record, or a token path to a relation.
    public string? Target { get; set; }
    /// <summary>The fields and values written.</summary>
    public List<FieldValue> Values { get; set; } = [];
}

/// <summary>Creates a record (spec §7 createRecord); its id is the output <c>id</c>. A reset can delete it again.</summary>
public sealed class CreateRecordStep : Step
{
    /// <summary>The registry entity key of the record to create.</summary>
    public string Entity { get; set; } = "";
    /// <summary>Its settable fields and values (a relation field linking it to this record is the usual one).</summary>
    public List<FieldValue> Values { get; set; } = [];
}

/// <summary>One variable a <c>setVariables</c> step sets.</summary>
public sealed class VariableValue
{
    /// <summary>The variable's id.</summary>
    public string Variable { get; set; } = "";
    /// <summary>The new value.</summary>
    public Operand Value { get; set; } = Operand.Of(null);
}

/// <summary>Sets workflow variables.</summary>
public sealed class SetVariablesStep : Step
{
    /// <summary>The variables and values.</summary>
    public List<VariableValue> Values { get; set; } = [];
}

/// <summary>Applies a lifecycle transition to the record or a related one (output <c>applied</c>).</summary>
public sealed class ApplyTransitionStep : Step
{
    /// <c>self</c>/null for the workflow's record, or a token path to a relation.
    public string? Target { get; set; }
    /// <summary>The lifecycle definition key.</summary>
    public string Lifecycle { get; set; } = "";
    /// <summary>The transition key.</summary>
    public string Transition { get; set; } = "";
    /// <summary>Answers for the transition's input form, by key.</summary>
    public Dictionary<string, Operand>? Input { get; set; }
    /// <summary>Remarks recorded with the move (<c>trigger.comment</c>); the transition's required check does not apply to a workflow.</summary>
    public Template? Comment { get; set; }
    /// <c>fail</c> (default) or <c>skip</c> (output <c>applied</c> false) when the record is not in a from state, the guard fails or the lifecycle is not the record's own.
    public string? IfNotAllowed { get; set; }
}

/// <summary>Queues a message on a channel through the outbox (output <c>sent</c>: number of recipients).</summary>
public sealed class NotifyStep : Step
{
    /// <summary>The registry channel key.</summary>
    public string Channel { get; set; } = "";
    /// <summary>Recipients: people (spec §8) and <c>{email}</c> address templates.</summary>
    public List<Assignee> To { get; set; } = [];
    /// <summary>The subject line, where the channel has one.</summary>
    public Template? Subject { get; set; }
    /// <summary>The message text.</summary>
    public Template Body { get; set; } = new();
    /// <c>record</c> for a link to the record, or a URL template.
    [JsonConverter(typeof(TemplateConverter))] public Template? Link { get; set; }
}

/// <summary>Calls an HTTP endpoint on a named connection or an allowed origin (outputs <c>status</c>, <c>body</c>).</summary>
public sealed class WebhookStep : Step
{
    /// <summary>A connection key from settings (its base URL and secret); null when <see cref="Url"/> is used.</summary>
    public string? Connection { get; set; }
    /// <summary>An absolute URL whose origin must be allowed in settings.</summary>
    public string? Url { get; set; }
    /// <summary>The HTTP method (default <c>POST</c>), one of <see cref="Methods"/>.</summary>
    public string Method { get; set; } = "POST";

    /// <summary>The HTTP methods a webhook may use, in capitals as sent.</summary>
    public static readonly IReadOnlyList<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    /// <summary>Appended to the connection or URL; values it inserts are URL-encoded.</summary>
    public Template? Path { get; set; }
    /// <summary>Extra request headers.</summary>
    public Dictionary<string, Template>? Headers { get; set; }
    /// <summary>JSON body; <c>{"ref": path}</c> objects are replaced by their values.</summary>
    public JsonNode? Body { get; set; }
    /// <summary>Statuses that count as success; null = any 2xx.</summary>
    public List<int>? Expect { get; set; }
}

/// <summary>Calls an action the app registered (outputs: the action's declared outputs).</summary>
public sealed class ActionStep : Step
{
    /// <summary>The registry action key.</summary>
    public string Action { get; set; } = "";
    /// <summary>Input values by key.</summary>
    public Dictionary<string, Operand>? Input { get; set; }
}

/// <summary>Starts another automation (outputs <c>instanceId</c>, <c>status</c>, <c>outputs</c>).</summary>
public sealed class SubWorkflowStep : Step
{
    /// <summary>The automation's key.</summary>
    public string Workflow { get; set; } = "";
    /// <c>self</c>/null for the same record, or a token path to a relation.
    public string? Target { get; set; }
    /// <summary>When true the step waits for the child to finish.</summary>
    public bool Wait { get; set; }
    /// <summary>Values the child reads as <c>input.*</c>.</summary>
    public Dictionary<string, Operand>? Input { get; set; }
}

/// <summary>Jumps back to an earlier step on the current path, at most <see cref="MaxTimes"/> times (output <c>count</c>).</summary>
public sealed class GoToStep : Step
{
    /// <summary>The id of the step to go back to.</summary>
    public string Target { get; set; } = "";
    /// <summary>How many jumps (1–50); afterwards the step is skipped.</summary>
    public int MaxTimes { get; set; } = 1;
}

/// <summary>Ends the run as completed or failed.</summary>
public sealed class EndStep : Step
{
    /// <c>completed</c> (default) or <c>failed</c>.
    public string Result { get; set; } = "completed";
    /// <summary>Why, recorded on the instance.</summary>
    public Template? Reason { get; set; }
}

/// <summary>A step type this engine does not know (from a newer spec). Lint reports it; running it fails.</summary>
public sealed class UnknownStep : Step;

/// <summary>The step types this engine runs, by their JSON <c>type</c>.</summary>
public static class StepTypes
{
    /// <summary>Step type name → model class.</summary>
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
        ["createRecord"] = typeof(CreateRecordStep),
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
