namespace Zouriel.Workflows.Storage;

// Persistence records. Engines store these in the host database (EF Core) or in memory (tests and
// test runs). JSON columns hold workflow values serialised with Json.Values.

/// <summary>The lifecycle of a workflow definition (spec §13).</summary>
public static class DefinitionStatus
{
    /// <summary>Never published; only the draft exists.</summary>
    public const string Draft = "draft";
    /// <summary>Has at least one published version whose triggers are live.</summary>
    public const string Published = "published";
    /// <summary>Starts nothing new; running instances finish on their version.</summary>
    public const string Archived = "archived";
}

/// <summary>A workflow definition: its editable draft and which version is published.</summary>
public sealed class DefinitionRecord
{
    /// <summary>The definition's immutable id (from the JSON <c>id</c>).</summary>
    public string Id { get; set; } = "";
    /// <summary>The unique key other workflows and the API use (fixed once published).</summary>
    public string Key { get; set; } = "";
    /// <c>automation</c> or <c>lifecycle</c>.
    public string Kind { get; set; } = "";
    /// <summary>The name people see.</summary>
    public string Title { get; set; } = "";
    /// <summary>The registry entity the workflow is about, or null for record-less automations.</summary>
    public string? Entity { get; set; }
    /// <c>draft</c>, <c>published</c> or <c>archived</c> (<see cref="DefinitionStatus"/>).
    public string Status { get; set; } = DefinitionStatus.Draft;
    /// <summary>The latest published version (0 when never published).</summary>
    public int Version { get; set; }
    /// <summary>The current draft, as normalised JSON.</summary>
    public string DraftJson { get; set; } = "{}";
    /// <summary>True when the draft differs from the latest published version.</summary>
    public bool HasDraftChanges { get; set; } = true;
    /// <summary>When the definition was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>The user id that created it (null for system).</summary>
    public string? CreatedBy { get; set; }
    /// <summary>When the draft or status last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>The user id that last changed it.</summary>
    public string? UpdatedBy { get; set; }
    /// <summary>Optimistic concurrency token, incremented on every update.</summary>
    public long RowVersion { get; set; }
}

/// <summary>An immutable published version of a definition.</summary>
public sealed class DefinitionVersionRecord
{
    /// <summary>The definition this version belongs to.</summary>
    public string DefinitionId { get; set; } = "";
    /// <summary>The version number (1, 2, …).</summary>
    public int Version { get; set; }
    /// <summary>The definition key at the time of publishing.</summary>
    public string Key { get; set; } = "";
    /// <summary>The published definition JSON (with <c>revision</c> set to the version).</summary>
    public string Json { get; set; } = "{}";
    /// <summary>SHA-256 of the canonical JSON, to tell whether a draft differs.</summary>
    public string Hash { get; set; } = "";
    /// <summary>The publisher's change note.</summary>
    public string? Note { get; set; }
    /// <summary>When this version was published.</summary>
    public DateTimeOffset PublishedAt { get; set; }
    /// <summary>The user id that published it.</summary>
    public string? PublishedBy { get; set; }
}

/// <summary>One trigger of a published definition, indexed for cheap matching (spec §5).</summary>
public sealed class TriggerRecord
{
    /// <summary>Row id.</summary>
    public string Id { get; set; } = "";
    /// <summary>The definition whose trigger this is.</summary>
    public string DefinitionId { get; set; } = "";
    /// <summary>That definition's key.</summary>
    public string DefinitionKey { get; set; } = "";
    /// <summary>The published version the trigger belongs to.</summary>
    public int Version { get; set; }
    /// <summary>The trigger type (spec §5), or <c>lifecycle</c> for a lifecycle's owned field.</summary>
    public string Type { get; set; } = "";
    /// <summary>The entity it watches, if any.</summary>
    public string? Entity { get; set; }
    /// <summary>The field it watches (fieldChanged, dateReached) or owns (lifecycle).</summary>
    public string? Field { get; set; }
    /// <summary>For stateEntered/transitionApplied: the lifecycle definition key.</summary>
    public string? Lifecycle { get; set; }
    /// <summary>For signal triggers: the signal key.</summary>
    public string? Signal { get; set; }
    /// <summary>When the definition was first published; changes committed before it never start it (spec §5).</summary>
    public DateTimeOffset PublishedAt { get; set; }
    /// <summary>Extra matching data (JSON): lifecycle mode and initial state, schedule cron…</summary>
    public string? Data { get; set; }
}

/// <summary>The kinds of instance.</summary>
public static class InstanceKinds
{
    /// <summary>An automation run started by a trigger.</summary>
    public const string Automation = "automation";
    /// <summary>The long-lived lifecycle instance of one record.</summary>
    public const string Record = "record";
    /// <summary>A child run of a record instance (transition steps, onEnter, onExit, SLA).</summary>
    public const string Run = "run";
}

/// <summary>Instance statuses (spec §10).</summary>
public static class InstanceStatus
{
    /// <summary>Waiting for an earlier instance of the same workflow and record to finish.</summary>
    public const string Queued = "queued";
    /// <summary>Executing steps right now.</summary>
    public const string Running = "running";
    /// <summary>Paused on a person, a timer, a signal or a condition.</summary>
    public const string Waiting = "waiting";
    /// <summary>Stopped on a failed step until an admin retries, skips or cancels.</summary>
    public const string Incident = "incident";
    /// <summary>Finished normally (record instances: reached a terminal state).</summary>
    public const string Completed = "completed";
    /// <summary>Ended by an <c>end</c> step with result failed.</summary>
    public const string Failed = "failed";
    /// <summary>Stopped by an admin, a cancelled parent or a deleted record.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>Record instances: in a non-terminal state.</summary>
    public const string Active = "active";
    /// <summary>Record instances: the field holds a value the lifecycle does not agree with.</summary>
    public const string OutOfSync = "outOfSync";

    /// <summary>True for completed, failed and cancelled.</summary>
    public static bool IsFinished(string s) => s is Completed or Failed or Cancelled;
}

/// <summary>One instance: an automation run, a lifecycle record instance or a child run.</summary>
public sealed class InstanceRecord
{
    /// <summary>The instance id.</summary>
    public string Id { get; set; } = "";
    /// <summary>The definition it runs.</summary>
    public string DefinitionId { get; set; } = "";
    /// <summary>That definition's key.</summary>
    public string DefinitionKey { get; set; } = "";
    /// <summary>The definition version it is pinned to.</summary>
    public int Version { get; set; }
    /// <see cref="InstanceKinds"/>.
    public string Kind { get; set; } = InstanceKinds.Automation;
    /// <summary>Child runs: the record instance; waited-for sub-workflows: the parent instance.</summary>
    public string? ParentId { get; set; }
    /// <summary>For runs: which list they execute (<c>transition:ID</c>, <c>onEnter:ID</c>, <c>onExit:ID</c>, <c>sla:ID</c>); for sub-workflows, the parent step.</summary>
    public string? Reason { get; set; }
    /// <summary>The record's entity, or null for record-less automations.</summary>
    public string? Entity { get; set; }
    /// <summary>The record's id.</summary>
    public string? EntityId { get; set; }
    /// <see cref="InstanceStatus"/>.
    public string Status { get; set; } = InstanceStatus.Running;
    /// <summary>Record instances: the current state id.</summary>
    public string? State { get; set; }
    /// <summary>Record instances: when the current state was entered.</summary>
    public DateTimeOffset? StateEnteredAt { get; set; }
    /// <summary>The interpreter's tokens, outputs and variables (JSON).</summary>
    public string Data { get; set; } = "{}";
    /// <summary>What started the instance (<c>trigger.*</c>), as JSON.</summary>
    public string? TriggerJson { get; set; }
    /// <summary>Loop protection depth (spec §5): 0 for people and schedules, +1 per workflow-caused change.</summary>
    public int Depth { get; set; }
    /// <summary>The person who started it, or null for system starts.</summary>
    public string? InitiatorId { get; set; }
    /// <summary>That person's name.</summary>
    public string? InitiatorName { get; set; }
    /// <summary>The latest incident or end reason.</summary>
    public string? Error { get; set; }
    /// <summary>The step that failed, while in incident.</summary>
    public string? ErrorStepId { get; set; }
    /// <summary>Groups the audit entries of one chain of work.</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>When it was last saved.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>When it finished, if it has.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Optimistic concurrency token, incremented on every update.</summary>
    public long RowVersion { get; set; }
}

/// <summary>Statuses of one step execution.</summary>
public static class StepStatus
{
    /// <summary>Executing.</summary>
    public const string Running = "running";
    /// <summary>Paused (a task, timer, join or child workflow).</summary>
    public const string Waiting = "waiting";
    /// <summary>Finished successfully.</summary>
    public const string Completed = "completed";
    /// <summary>The attempt failed.</summary>
    public const string Failed = "failed";
    /// <summary>Not run (disabled, or skipped by an admin).</summary>
    public const string Skipped = "skipped";
    /// <summary>Stopped because its path or instance was cancelled.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>One execution attempt of a step, for run views.</summary>
public sealed class StepRecord
{
    /// <summary>Row id.</summary>
    public string Id { get; set; } = "";
    /// <summary>The instance it ran in.</summary>
    public string InstanceId { get; set; } = "";
    /// <summary>The step id in the definition.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>The step type (<c>action</c>, <c>approval</c>, …).</summary>
    public string StepType { get; set; } = "";
    /// <summary>The token (thread) that ran it.</summary>
    public string? TokenId { get; set; }
    /// <summary>1-based attempt number (retries count up).</summary>
    public int Attempt { get; set; } = 1;
    /// <see cref="StepStatus"/>.
    public string Status { get; set; } = StepStatus.Running;
    /// <summary>What the step was given, as JSON.</summary>
    public string? Input { get; set; }
    /// <summary>Its outputs, as JSON.</summary>
    public string? Output { get; set; }
    /// <summary>The failure message, if it failed.</summary>
    public string? Error { get; set; }
    /// <summary>
    /// What it changed, as JSON, so a reset can take it back (spec §6 reset): <c>fields</c> (entity, id, before,
    /// after), <c>created</c> (entity, id, values), <c>action</c> (action, input, output), <c>message</c>, <c>webhook</c>
    /// or <c>move</c> (entity, id, lifecycle, transition). Every entry has a <c>seq</c> (its order) and, once a reset
    /// has considered it, <c>settled</c>. Unmasked (the exact values to restore); never shown as is.
    /// </summary>
    public string? Undo { get; set; }
    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>When it ended, if it has.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>What a bookmark waits for.</summary>
public static class BookmarkKinds
{
    /// <summary>A point in time (waits, retries, task deadlines, automatic transitions, SLAs).</summary>
    public const string Timer = "timer";
    /// <summary>A person's decision.</summary>
    public const string Task = "task";
    /// <summary>An app-raised signal.</summary>
    public const string Signal = "signal";
    /// <summary>A condition on the record becoming true.</summary>
    public const string Condition = "condition";
    /// <summary>A sub-workflow finishing.</summary>
    public const string Child = "child";
    /// <summary>A schedule trigger's next run (no instance).</summary>
    public const string Schedule = "schedule";
    /// <summary>A dateReached trigger's time for one record (no instance).</summary>
    public const string DateReached = "dateReached";
}

/// <summary>What a bookmark does when it fires (<see cref="BookmarkRecord.Purpose"/>).</summary>
public static class BookmarkPurposes
{
    /// <summary>A <c>wait</c> step's timer ran out.</summary>
    public const string Wait = "wait";
    /// <summary>A condition or signal wait timed out.</summary>
    public const string Timeout = "timeout";
    /// <summary>A failed step's next attempt is due.</summary>
    public const string Retry = "retry";
    /// <summary>A task's due time passed.</summary>
    public const string Due = "due";
    /// <summary>A task reminder is due (payload: which reminder).</summary>
    public const string Reminder = "reminder";
    /// <summary>A task escalation is due.</summary>
    public const string Escalation = "escalation";
    /// <summary>A waited-for sub-workflow (payload: its instance id).</summary>
    public const string Child = "child";
    /// <summary>A condition wait, re-checked after each change to the record.</summary>
    public const string Condition = "condition";
    /// <summary>A signal wait.</summary>
    public const string Signal = "signal";
    /// <summary>A schedule trigger's next run.</summary>
    public const string Schedule = "schedule";
    /// <summary>A dateReached trigger's time for one record.</summary>
    public const string DateReached = "dateReached";
    /// <summary>Prefix of an automatic transition's timer (<c>auto:TRANSITION_ID</c>).</summary>
    public const string AutoPrefix = "auto:";
    /// <summary>Prefix of a state's SLA timer (<c>sla:STATE_ID</c>).</summary>
    public const string SlaPrefix = "sla:";
}

/// <summary>A durable pause point: a timer, a person, a signal or a condition.</summary>
public sealed class BookmarkRecord
{
    /// <summary>Row id.</summary>
    public string Id { get; set; } = "";
    /// <summary>The waiting instance, or null for trigger bookmarks.</summary>
    public string? InstanceId { get; set; }
    /// <summary>The waiting token.</summary>
    public string? TokenId { get; set; }
    /// <summary>The step that waits.</summary>
    public string? NodeId { get; set; }
    /// <see cref="BookmarkKinds"/>.
    public string Kind { get; set; } = BookmarkKinds.Timer;
    /// <summary>What happens when it fires: <c>wait</c>, <c>due</c>, <c>reminder</c>, <c>escalation</c>, <c>retry</c>, <c>timeout</c>, <c>auto:TRANSITION</c>, <c>sla:STATE</c>, <c>schedule</c>, <c>dateReached</c>.</summary>
    public string Purpose { get; set; } = "wait";
    /// <summary>When a timer fires; null for non-timer bookmarks.</summary>
    public DateTimeOffset? DueAt { get; set; }
    /// <summary>For signal waits: the signal key.</summary>
    public string? Signal { get; set; }
    /// <summary>The record's entity (null for waits about no record).</summary>
    public string? Entity { get; set; }
    /// <summary>The record's id.</summary>
    public string? EntityId { get; set; }
    /// <summary>The definition, for trigger bookmarks and lookups.</summary>
    public string? DefinitionId { get; set; }
    /// <summary>For task timers (due, reminder, escalation): the task.</summary>
    public string? TaskId { get; set; }
    /// <summary>Purpose-specific data (a child instance id, a reminder index, JSON).</summary>
    public string? Payload { get; set; }
    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Set while a worker processes it, so two workers never fire the same bookmark.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>Statuses of a person's task.</summary>
public static class TaskStatus
{
    /// <summary>Waiting for decisions.</summary>
    public const string Open = "open";
    /// <summary>Settled by decisions.</summary>
    public const string Completed = "completed";
    /// <summary>Withdrawn because its path or instance was cancelled.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>Its due time passed before it was settled.</summary>
    public const string TimedOut = "timedOut";
}

/// <summary>A task for people: an approval or a task step with outcomes.</summary>
public sealed class TaskRecord
{
    /// <summary>The task id.</summary>
    public string Id { get; set; } = "";
    /// <summary>The instance whose step created it.</summary>
    public string InstanceId { get; set; } = "";
    /// <summary>The automation or record instance it belongs to (for child runs, the record instance).</summary>
    public string? RootInstanceId { get; set; }
    /// <summary>The step id.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>The waiting token.</summary>
    public string TokenId { get; set; } = "";
    /// <c>approval</c> or <c>task</c>.
    public string Kind { get; set; } = "approval";
    /// <summary>What people see in their inbox.</summary>
    public string Title { get; set; } = "";
    /// <summary>The rendered instructions.</summary>
    public string? Instructions { get; set; }
    /// <summary>The workflow's key.</summary>
    public string DefinitionKey { get; set; } = "";
    /// <summary>The record's entity.</summary>
    public string? Entity { get; set; }
    /// <summary>The record's id.</summary>
    public string? EntityId { get; set; }
    /// <summary>The record's title when the task was created.</summary>
    public string? EntityTitle { get; set; }
    /// <see cref="TaskStatus"/>.
    public string Status { get; set; } = TaskStatus.Open;
    /// <summary>JSON array of user ids who may decide.</summary>
    public string Assignees { get; set; } = "[]";
    /// <summary>JSON: <c>{type, value}</c> for approvals.</summary>
    public string? Rule { get; set; }
    /// <summary>JSON array of <c>{key, label, tone}</c>.</summary>
    public string Outcomes { get; set; } = "[]";
    /// <summary>JSON array of decisions <c>{userId, name, outcome, comment, data, at}</c>.</summary>
    public string Decisions { get; set; } = "[]";
    /// <summary>The form to fill in when deciding (form-builder JSON).</summary>
    public string? Form { get; set; }
    /// <c>never</c>, <c>reject</c> or <c>always</c>.
    public string? RequireComment { get; set; }
    /// <summary>Whether assignees may hand the task to someone else.</summary>
    public bool AllowDelegation { get; set; }
    /// <summary>The settled outcome key, once completed or timed out.</summary>
    public string? Outcome { get; set; }
    /// <summary>When it times out.</summary>
    public DateTimeOffset DueAt { get; set; }
    /// <summary>How many times it has been escalated.</summary>
    public int EscalationLevel { get; set; }
    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When it was settled, cancelled or timed out.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Optimistic concurrency token, incremented on every update.</summary>
    public long RowVersion { get; set; }
}

/// <summary>What an outbox row carries.</summary>
public static class OutboxKinds
{
    /// <summary>A committed record change (<c>EntityChange</c>).</summary>
    public const string Change = "change";
    /// <summary>A lifecycle transition (<c>LifecycleEvent</c>).</summary>
    public const string Lifecycle = "lifecycle";
    /// <summary>A notification to deliver.</summary>
    public const string Notify = "notify";
    /// <summary>Reserved for resuming an instance after commit.</summary>
    public const string Resume = "resume";
}

/// <summary>A row of the transactional outbox: work done after the unit that wrote it commits.</summary>
public sealed class OutboxRecord
{
    /// <summary>Row id.</summary>
    public string Id { get; set; } = "";
    /// <see cref="OutboxKinds"/>.
    public string Kind { get; set; } = OutboxKinds.Change;
    /// <summary>The event or notification, as JSON.</summary>
    public string Payload { get; set; } = "{}";
    /// <summary>When it was written.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When it was handled; null while pending.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }
    /// <summary>How many times processing was tried.</summary>
    public int Attempts { get; set; }
    /// <summary>Claimed by a worker until then (or parked for an admin after 10 failures).</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>The last processing error.</summary>
    public string? Error { get; set; }
}

/// <summary>One hash-chained audit entry (spec §11).</summary>
public sealed class AuditRecord
{
    /// <summary>Position in the tenant's chain (1, 2, …).</summary>
    public long Seq { get; set; }
    /// <summary>Entry id.</summary>
    public string Id { get; set; } = "";
    /// <summary>When it happened (UTC, millisecond precision).</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>The chain it belongs to, or null without tenancy.</summary>
    public string? Tenant { get; set; }
    /// <c>user</c>, <c>system</c> or <c>workflow</c>.
    public string ActorType { get; set; } = "system";
    /// <summary>The user id, or the instance id for workflows.</summary>
    public string? ActorId { get; set; }
    /// <summary>The actor's display name (workflow key for workflows).</summary>
    public string? ActorName { get; set; }
    /// <summary>The audit category (<c>definition</c>, <c>instance</c>, <c>step</c>, …).</summary>
    public string Category { get; set; } = "";
    /// <summary>The action within the category (<c>started</c>, <c>decided</c>, …).</summary>
    public string Action { get; set; } = "";
    /// <summary>What the entry is about (<c>instance</c>, <c>task</c>, <c>record</c>, …).</summary>
    public string SubjectType { get; set; } = "";
    /// <summary>That subject's id.</summary>
    public string SubjectId { get; set; } = "";
    /// <summary>The record's entity, when it concerns a record.</summary>
    public string? Entity { get; set; }
    /// <summary>The record's id.</summary>
    public string? EntityId { get; set; }
    /// <summary>The instance concerned, if any.</summary>
    public string? InstanceId { get; set; }
    /// <summary>The definition concerned, if any.</summary>
    public string? DefinitionId { get; set; }
    /// <summary>Masked JSON of the state before.</summary>
    public string? Before { get; set; }
    /// <summary>Masked JSON of the state after.</summary>
    public string? After { get; set; }
    /// <summary>Masked JSON of the details.</summary>
    public string? Data { get; set; }
    /// <summary>Groups entries of one chain of work.</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>The instance or entry that caused this one, if known.</summary>
    public string? CausationId { get; set; }
    /// <summary>The previous entry's hash (64 zeros for the first).</summary>
    public string PrevHash { get; set; } = "";
    /// <c>sha256_hex(prevHash + "\n" + jcs(entry))</c>.
    public string Hash { get; set; } = "";
}

/// <summary>The stored global settings (spec §12), with a revision for optimistic updates.</summary>
public sealed class SettingsRecord
{
    /// <summary>Always <c>global</c>.</summary>
    public string Key { get; set; } = "global";
    /// <summary>The settings JSON.</summary>
    public string Json { get; set; } = "{\"version\":1}";
    /// <summary>Incremented on every save; a save naming an older revision is refused.</summary>
    public int Revision { get; set; }
    /// <summary>When it was last saved.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>The user id that last saved it.</summary>
    public string? UpdatedBy { get; set; }
}
