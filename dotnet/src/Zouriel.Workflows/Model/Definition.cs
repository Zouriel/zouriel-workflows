using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zouriel.Workflows.Model;

/// <summary>A workflow definition (spec §1–§7). Parsed from the JSON the builder writes.</summary>
public sealed class WorkflowDefinition
{
    /// <summary>The spec version the definition was written for (currently <c>1</c>).</summary>
    public int SpecVersion { get; set; } = 1;
    /// <summary>Immutable generated id of the definition (<c>[A-Za-z0-9_-]{1,64}</c>).</summary>
    public string Id { get; set; } = "";
    /// <summary>Editable unique key that people, APIs and <c>subWorkflow</c> steps use.</summary>
    public string Key { get; set; } = "";
    /// <c>automation</c> or <c>lifecycle</c> (see <see cref="WorkflowKinds"/>).
    public string Kind { get; set; } = WorkflowKinds.Automation;
    /// <summary>The name shown to people.</summary>
    public string Title { get; set; } = "";
    /// <summary>Optional longer explanation for admins.</summary>
    public string? Description { get; set; }
    /// <summary>The published version number stamped on a published copy; null in a draft that was never published.</summary>
    public int? Revision { get; set; }
    /// <summary>The registry entity the workflow is about (its record is <c>entity.*</c>); null for schedules and signals with no record.</summary>
    public string? Entity { get; set; }
    /// <summary>What starts an automation (spec §5); null for lifecycles.</summary>
    public Trigger? Trigger { get; set; }
    /// <summary>The steps an automation runs, in order (spec §7); null for lifecycles.</summary>
    public List<Step>? Steps { get; set; }
    /// <summary>The states and transitions of a lifecycle (spec §6); null for automations.</summary>
    public Lifecycle? Lifecycle { get; set; }
    /// <summary>Workflow variables (<c>vars.*</c>) with their starting values.</summary>
    public List<Variable>? Variables { get; set; }
    /// <summary>Per-workflow settings that override the global defaults.</summary>
    public WorkflowSettings? Settings { get; set; }

    /// <summary>True when <see cref="Kind"/> is <c>lifecycle</c>.</summary>
    [JsonIgnore] public bool IsLifecycle => Kind == WorkflowKinds.Lifecycle;
}

/// <summary>The two kinds of workflow definition.</summary>
public static class WorkflowKinds
{
    /// <summary>A trigger followed by steps.</summary>
    public const string Automation = "automation";
    /// <summary>States and transitions owning one enum field of a record.</summary>
    public const string Lifecycle = "lifecycle";
}

/// <summary>A workflow variable, read as <c>vars.&lt;key&gt;</c> and written by <c>setVariables</c>.</summary>
public sealed class Variable
{
    /// <summary>Immutable id; <c>setVariables</c> refers to variables by it.</summary>
    public string Id { get; set; } = "";
    /// <summary>The name used in token paths (<c>vars.&lt;key&gt;</c>).</summary>
    public string Key { get; set; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; set; } = "";
    /// <summary>A registry field type (default <c>text</c>) used to type the token.</summary>
    public string Type { get; set; } = "text";
    /// <summary>The value at the start of each run (null when unset).</summary>
    public JsonNode? Default { get; set; }
}

/// <summary>Settings of one workflow that override the global defaults (spec §12).</summary>
public sealed class WorkflowSettings
{
    /// <c>perRecordSerial</c>, <c>parallel</c> or <c>skipIfRunning</c> (spec §5); null uses the global default.
    public string? Concurrency { get; set; }
    /// <summary>People responsible for the workflow, notified about incidents when asked to.</summary>
    public List<Assignee>? Owners { get; set; }
    /// <summary>When true, owners get a notification each time a run stops in an incident.</summary>
    public bool? NotifyOwnersOnIncident { get; set; }
    /// <summary>Default calendar for durations without one: <c>working</c> or <c>calendar</c> (null = calendar time).</summary>
    public string? Calendar { get; set; }
}

/// <summary>The trigger <c>type</c> values (spec §5).</summary>
public static class TriggerTypes
{
    /// <summary>A record of the entity was created and committed.</summary>
    public const string Created = "created";
    /// <summary>A committed change moved a field into a matching value.</summary>
    public const string FieldChanged = "fieldChanged";
    /// <summary>A lifecycle entered one of the listed states.</summary>
    public const string StateEntered = "stateEntered";
    /// <summary>One of the listed lifecycle transitions was applied.</summary>
    public const string TransitionApplied = "transitionApplied";
    /// <summary>A record was deleted (<c>entity.*</c> reads the snapshot).</summary>
    public const string Deleted = "deleted";
    /// <summary>A date field plus an offset arrived, once per record.</summary>
    public const string DateReached = "dateReached";
    /// <summary>A cron schedule fired, optionally once per matching record.</summary>
    public const string Schedule = "schedule";
    /// <summary>A person started it from a record or the app.</summary>
    public const string Manual = "manual";
    /// <summary>The app raised a registered signal.</summary>
    public const string Signal = "signal";
}

/// <summary>What starts an automation (spec §5). Which properties apply depends on <see cref="Type"/>.</summary>
public sealed class Trigger
{
    /// <summary>One of <see cref="TriggerTypes"/>.</summary>
    public string Type { get; set; } = "";
    /// <summary>Extra condition checked against the record after the change; null always holds.</summary>
    public Condition? When { get; set; }
    /// <summary>When true, the workflow's own writes may start it again (loop protection still applies).</summary>
    public bool? Retrigger { get; set; }
    /// <c>fieldChanged</c> and <c>dateReached</c>: the watched field's key.
    public string? Field { get; set; }
    /// <c>fieldChanged</c>: the old value must be one of these; null or empty = any.
    public List<JsonNode?>? From { get; set; }
    /// <c>fieldChanged</c>: the new value must be one of these; null or empty = any change.
    public List<JsonNode?>? To { get; set; }
    /// <c>stateEntered</c>/<c>transitionApplied</c>: the lifecycle definition key.
    public string? Lifecycle { get; set; }
    /// <c>stateEntered</c>: the state keys that fire it.
    public List<string>? States { get; set; }
    /// <c>transitionApplied</c>: the transition keys that fire it.
    public List<string>? Transitions { get; set; }
    /// <c>dateReached</c>: signed ISO duration added to the date (e.g. <c>-P3D</c> = three days before).
    public string? Offset { get; set; }
    /// <c>dateReached</c> on a date field: time of day <c>HH:MM</c> in the settings time zone (default 09:00).
    public string? At { get; set; }
    /// <c>schedule</c>: a five-field cron expression in the settings time zone.
    public string? Cron { get; set; }
    /// <c>schedule</c>: start one instance per matching record instead of one with no record.
    public ScheduleForEach? ForEach { get; set; }
    /// <c>manual</c>: the button text.
    public string? Label { get; set; }
    /// <c>manual</c>: an optional form-builder form; its answers are <c>input.*</c>.
    public JsonObject? Form { get; set; }
    /// <c>manual</c>: who may start it; null or empty = any signed-in person.
    public List<Assignee>? Who { get; set; }
    /// <c>signal</c>: the registered signal key.
    public string? Signal { get; set; }
}

/// <summary>Makes a schedule start one instance per record of the workflow's entity.</summary>
public sealed class ScheduleForEach
{
    /// <summary>Only records matching this condition get an instance; null = all.</summary>
    public Condition? When { get; set; }
    /// <summary>The most instances one run of the schedule starts (default 100, at most 500).</summary>
    public int Limit { get; set; } = 100;
}

/// <summary>A lifecycle: the enum field it owns, its states and its transitions (spec §6).</summary>
public sealed class Lifecycle
{
    /// <summary>The <c>lifecycle: true</c> enum field the lifecycle owns.</summary>
    public string Field { get; set; } = "";
    /// <c>enforce</c> (default: outside writes are refused) or <c>observe</c> (outside writes are accepted and recorded).
    public string? Mode { get; set; }
    /// <summary>The states; each maps to one value of the field by key.</summary>
    public List<State> States { get; set; } = [];
    /// <summary>The transitions between states.</summary>
    public List<Transition> Transitions { get; set; } = [];
    /// <summary>Editor positions of states in the diagram (state id → [x, y]); ignored by engines.</summary>
    public Dictionary<string, double[]>? Layout { get; set; }
    /// <summary>Lets people start a record's workflow over (spec §6 reset); absent = no reset.</summary>
    public LifecycleReset? Reset { get; set; }
    /// <summary>
    /// Which records the lifecycle is for (spec §6 applies to), read from the record's own fields when it
    /// gets its lifecycle; absent = every record (the fallback when several lifecycles share the field).
    /// </summary>
    public Condition? AppliesWhen { get; set; }

    /// <summary>True unless <see cref="Mode"/> is <c>observe</c>.</summary>
    [JsonIgnore] public bool Enforce => Mode != "observe";
}

/// <summary>
/// Starting a record's workflow over (spec §6): the people in <see cref="Who"/> may move the record back
/// to <see cref="To"/> (the initial state by default). Its open tasks and runs are cancelled first, then
/// <see cref="Steps"/> run, which can change this record and related ones.
/// </summary>
public sealed class LifecycleReset
{
    /// <summary>The button text (default "Reset workflow").</summary>
    public string? Label { get; set; }
    /// <summary>The state id to go back to; null = the initial state.</summary>
    public string? To { get; set; }
    /// <summary>Who may reset by hand; null or empty = nobody (the API refuses).</summary>
    public List<Assignee>? Who { get; set; }
    /// <summary>A confirmation question the record panel asks first.</summary>
    public string? Confirm { get; set; }
    /// <summary>A form (e.g. a reason) whose answers are validated and read as <c>input.*</c> by the steps.</summary>
    public TransitionInput? Input { get; set; }
    /// <summary>Run the current state's <c>onExit</c> and the target's <c>onEnter</c> (default false: a reset is not a move).</summary>
    public bool? RunExitEnter { get; set; }
    /// <summary>Also cancel running automations about the record (default true); the lifecycle's own runs always stop.</summary>
    public bool? CancelAutomations { get; set; }
    /// <summary>Remarks people give when they reset: <c>none</c>, <c>optional</c> (default) or <c>required</c>; steps read them as <c>trigger.comment</c>.</summary>
    public string? Comment { get; set; }
    /// <summary>The state ids the reset is offered in; null = every state (it is never offered when it would change nothing).</summary>
    public List<string>? States { get; set; }

    /// <summary>The remarks setting with its default applied.</summary>
    [JsonIgnore] public string ResolvedComment => Remarks.Resolve(Comment, Remarks.Optional);
    /// <summary>Steps run after the record is back in <see cref="To"/> (list id <c>reset</c>).</summary>
    public List<Step>? Steps { get; set; }
}

/// <summary>A lifecycle state; its key is the field value it stands for.</summary>
public sealed class State
{
    /// <summary>Immutable id that transitions refer to.</summary>
    public string Id { get; set; } = "";
    /// <summary>The field value this state stands for.</summary>
    public string Key { get; set; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; set; } = "";
    /// <c>initial</c> (exactly one), <c>active</c> or <c>terminal</c>.
    public string Category { get; set; } = "active";
    /// <summary>Colour hint for badges (e.g. <c>success</c>, <c>warning</c>, <c>danger</c>, <c>info</c>).</summary>
    public string? Tone { get; set; }
    /// <summary>Optional explanation for people.</summary>
    public string? Description { get; set; }
    /// <summary>Steps run as a child run each time the record enters the state.</summary>
    public List<Step>? OnEnter { get; set; }
    /// <summary>Steps run as a child run each time the record leaves the state.</summary>
    public List<Step>? OnExit { get; set; }
    /// <summary>When true, onEnter and SLA runs keep going after the record leaves the state.</summary>
    public bool? KeepRunsOnExit { get; set; }
    /// <summary>Steps to run once when the record stays in the state too long.</summary>
    public Sla? Sla { get; set; }

    /// <summary>True for terminal states (the record instance completes there).</summary>
    [JsonIgnore] public bool IsTerminal => Category == "terminal";
    /// <summary>True for the initial state.</summary>
    [JsonIgnore] public bool IsInitial => Category == "initial";
}

/// <summary>A service-level timer of a state: run steps once after <see cref="Within"/>.</summary>
public sealed class Sla
{
    /// <summary>ISO duration the record may stay in the state.</summary>
    public string Within { get; set; } = "";
    /// <c>working</c> to count working time only; null = calendar time.
    public string? Calendar { get; set; }
    /// <summary>Steps run as a child run when the time is up.</summary>
    public List<Step> Steps { get; set; } = [];
}

/// <summary>A move between lifecycle states (spec §6): who may apply it, its guard, input form and steps.</summary>
public sealed class Transition
{
    /// <summary>Immutable id (auto timers refer to it).</summary>
    public string Id { get; set; } = "";
    /// <summary>The key people and APIs apply it by.</summary>
    public string Key { get; set; } = "";
    /// <summary>The button text.</summary>
    public string Label { get; set; } = "";
    /// <summary>The states it can be applied from, or any non-terminal state (<c>"*"</c>).</summary>
    [JsonConverter(typeof(TransitionFromConverter))] public TransitionFrom From { get; set; } = new();
    /// <summary>The state id it leads to.</summary>
    public string To { get; set; } = "";
    /// <summary>Who may apply it by hand; null or empty = nobody (system and automations only).</summary>
    public List<Assignee>? Who { get; set; }
    /// <summary>Guard: when false the transition is neither offered nor applicable.</summary>
    public Condition? When { get; set; }
    /// <summary>A form whose answers are validated and read as <c>input.*</c>.</summary>
    public TransitionInput? Input { get; set; }
    /// <summary>Steps run as a child run after the state changes.</summary>
    public List<Step>? Steps { get; set; }
    /// <summary>Makes the system apply it after a time or when a condition holds.</summary>
    public AutoTransition? Auto { get; set; }
    /// <summary>A confirmation question the record panel asks before applying it.</summary>
    public string? Confirm { get; set; }
    /// <summary>Colour hint for the button.</summary>
    public string? Tone { get; set; }
    /// <summary>When true, people never see or apply it; only automations and the system do.</summary>
    public bool? Hidden { get; set; }
    /// <summary>Remarks people give when they apply it: <c>none</c>, <c>optional</c> or <c>required</c>; null = optional for <c>danger</c> moves, else none.</summary>
    public string? Comment { get; set; }

    /// <summary>The remarks setting with its default applied (never null).</summary>
    [JsonIgnore] public string ResolvedComment => Remarks.Resolve(Comment, Tone == "danger" ? Remarks.Optional : Remarks.None);
}

/// <summary>Remarks people give with a move or a reset (spec §6): whether they are asked, and the length limit.</summary>
public static class Remarks
{
    /// <summary>Not asked; anything sent is ignored.</summary>
    public const string None = "none";
    /// <summary>Asked, may be left empty.</summary>
    public const string Optional = "optional";
    /// <summary>A person must give them (<c>commentRequired</c> otherwise).</summary>
    public const string Required = "required";
    /// <summary>The longest remarks accepted, in characters.</summary>
    public const int MaxLength = 2000;

    /// <summary>A known setting, else <paramref name="fallback"/>.</summary>
    public static string Resolve(string? value, string fallback) => value is None or Optional or Required ? value : fallback;
}

/// <summary><c>"*"</c> (any non-terminal state) or a list of state ids.</summary>
public sealed class TransitionFrom
{
    /// <summary>True for <c>"*"</c>: every non-terminal state other than the target.</summary>
    public bool Any { get; set; }
    /// <summary>The state ids it can be applied from (when not <see cref="Any"/>).</summary>
    public List<string> States { get; set; } = [];

    /// <summary>Whether the transition can start from <paramref name="stateId"/>.</summary>
    /// <param name="lc">The lifecycle the transition belongs to.</param>
    /// <param name="stateId">The record's current state id.</param>
    /// <param name="toId">The transition's target state id (excluded for <c>"*"</c>).</param>
    public bool Allows(Lifecycle lc, string stateId, string toId)
    {
        if (!Any) return States.Contains(stateId);
        var st = lc.States.FirstOrDefault(s => s.Id == stateId);
        return st is not null && !st.IsTerminal && stateId != toId;
    }
}

/// <summary>The input form of a transition.</summary>
public sealed class TransitionInput
{
    /// <summary>A form-builder form definition.</summary>
    public JsonObject Form { get; set; } = new();
}

/// <summary>Automatic application of a transition: after a time in a <c>from</c> state, or when a condition holds.</summary>
public sealed class AutoTransition
{
    /// <summary>ISO duration after entering a <c>from</c> state; null when <see cref="When"/> is used.</summary>
    public string? After { get; set; }
    /// <c>working</c> to count working time only; null = calendar time.
    public string? Calendar { get; set; }
    /// <summary>Applied when this holds while in a <c>from</c> state (checked on entering and after each change).</summary>
    public Condition? When { get; set; }
}

// ── Assignees ─────────────────────────────────────────────────────────────

/// <summary>One entry of an assignee list (spec §8). Exactly one property is set.</summary>
public sealed class Assignee
{
    /// <summary>Everyone with this registry role.</summary>
    public string? Role { get; set; }
    /// <summary>Everyone in this registry group.</summary>
    public string? Group { get; set; }
    /// <summary>One user by id.</summary>
    public string? User { get; set; }
    /// <summary>The user in a user field (a token path such as <c>entity.reviewer</c>).</summary>
    public string? Field { get; set; }
    /// <summary>The manager of <c>initiator</c>, of the current <c>assignees</c>, or of the user at a token path.</summary>
    public string? ManagerOf { get; set; }
    /// <summary>When true, the person who started or applied it.</summary>
    public bool? Initiator { get; set; }
    /// <summary>Only for notification recipients: an address template.</summary>
    [JsonConverter(typeof(TemplateConverter))] public Template? Email { get; set; }
}

// ── Operands, templates, conditions ───────────────────────────────────────

/// <summary><c>{const}</c>, <c>{ref}</c>, <c>{today}</c> or <c>{calc}</c>.</summary>
[JsonConverter(typeof(OperandConverter))]
public sealed class Operand
{
    /// <summary>True for a <c>{const}</c> operand (the constant may be null).</summary>
    public bool HasConst { get; init; }
    /// <summary>The constant value, as a workflow value.</summary>
    public object? Const { get; init; }
    /// <summary>The token path of a <c>{ref}</c> operand.</summary>
    public string? Ref { get; init; }
    /// <summary>Days from today of a <c>{today}</c> operand.</summary>
    public int? Today { get; init; }
    /// <summary>The calculation of a <c>{calc}</c> operand.</summary>
    public Calc? Calc { get; init; }

    /// <summary>A constant operand.</summary>
    /// <param name="value">The constant (normalised to a workflow value).</param>
    public static Operand Of(object? value) => new() { HasConst = true, Const = Json.Values.Normalize(value) };
}

/// <summary>A calculation from the form engine (sum, difference, dates…), with token paths in its arguments.</summary>
public sealed class Calc
{
    /// <summary>The operation (default <c>sum</c>), as the form engine names it.</summary>
    public string Op { get; set; } = "sum";
    /// <summary>The arguments, in order.</summary>
    public List<CalcArg> Args { get; set; } = [];
    /// <summary>Round the result to this many decimals; null = no rounding.</summary>
    public int? Decimals { get; set; }
}

/// <summary>One argument of a calculation: a token path, a constant or a nested calculation.</summary>
[JsonConverter(typeof(CalcArgConverter))]
public sealed class CalcArg
{
    /// <summary>A token path.</summary>
    public string? Ref { get; init; }
    /// <summary>True when the argument is a constant.</summary>
    public bool HasConst { get; init; }
    /// <summary>The constant value.</summary>
    public object? Const { get; init; }
    /// <summary>A nested calculation.</summary>
    public Calc? Calc { get; init; }
}

/// <summary>Text with token parts (spec §3).</summary>
[JsonConverter(typeof(TemplateConverter))]
public sealed class Template
{
    /// <summary>Each part is a <see cref="string"/> or a token path wrapped in <see cref="TemplateRef"/>.</summary>
    public List<object> Parts { get; init; } = [];

    /// <summary>A template with one plain text part.</summary>
    /// <param name="text">The text.</param>
    public static Template Of(string text) => new() { Parts = [text] };
}

/// <summary>A token part of a template, rendered as text when the template is.</summary>
/// <param name="Ref">The token path.</param>
public sealed record TemplateRef(string Ref);

/// <summary>A condition rule or group, exactly the form builder's JSON (spec §4).</summary>
[JsonConverter(typeof(ConditionConverter))]
public abstract class Condition;

/// <summary>A group of conditions combined by <see cref="Match"/>; an empty group holds.</summary>
public sealed class ConditionGroup : Condition
{
    /// <c>all</c>, <c>any</c> or <c>none</c>.
    public string Match { get; set; } = "all";
    /// <summary>The rules and groups inside.</summary>
    public List<Condition> Conditions { get; set; } = [];
}

/// <summary>One comparison: the value at <see cref="Ref"/> against <see cref="Value"/> with <see cref="Op"/>.</summary>
public sealed class ConditionRule : Condition
{
    /// <summary>The token path compared.</summary>
    public string Ref { get; set; } = "";
    /// <summary>The form engine operator (e.g. <c>eq</c>, <c>anyOf</c>, <c>between</c>, <c>isTrue</c>).</summary>
    public string Op { get; set; } = "eq";
    /// <summary>The value compared with; null for operators that need none.</summary>
    public Operand? Value { get; set; }
    /// <summary>The second value of two-value operators (e.g. <c>between</c>).</summary>
    public Operand? Value2 { get; set; }
}
