namespace Zouriel.Workflows.Storage;

// Persistence records. Engines store these in the host database (EF Core) or in memory (tests and
// test runs). JSON columns hold workflow values serialised with Json.Values.

public static class DefinitionStatus
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";
}

public sealed class DefinitionRecord
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Entity { get; set; }
    public string Status { get; set; } = DefinitionStatus.Draft;
    /// <summary>The latest published version (0 when never published).</summary>
    public int Version { get; set; }
    public string DraftJson { get; set; } = "{}";
    /// <summary>True when the draft differs from the latest published version.</summary>
    public bool HasDraftChanges { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public long RowVersion { get; set; }
}

public sealed class DefinitionVersionRecord
{
    public string DefinitionId { get; set; } = "";
    public int Version { get; set; }
    public string Key { get; set; } = "";
    public string Json { get; set; } = "{}";
    public string Hash { get; set; } = "";
    public string? Note { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
}

/// <summary>One trigger of a published definition, indexed for cheap matching (spec §5).</summary>
public sealed class TriggerRecord
{
    public string Id { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string DefinitionKey { get; set; } = "";
    public int Version { get; set; }
    public string Type { get; set; } = "";
    public string? Entity { get; set; }
    public string? Field { get; set; }
    public string? Lifecycle { get; set; }
    public string? Signal { get; set; }
    /// <summary>When this version was published; changes committed before it never start it.</summary>
    public DateTimeOffset PublishedAt { get; set; }
    /// <summary>Extra matching data (JSON): lifecycle mode and initial state, schedule cron…</summary>
    public string? Data { get; set; }
}

public static class InstanceKinds
{
    /// <summary>An automation run started by a trigger.</summary>
    public const string Automation = "automation";
    /// <summary>The long-lived lifecycle instance of one record.</summary>
    public const string Record = "record";
    /// <summary>A child run of a record instance (transition steps, onEnter, onExit, SLA).</summary>
    public const string Run = "run";
}

public static class InstanceStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Incident = "incident";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Active = "active";
    public const string OutOfSync = "outOfSync";

    public static bool IsFinished(string s) => s is Completed or Failed or Cancelled;
}

public sealed class InstanceRecord
{
    public string Id { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string DefinitionKey { get; set; } = "";
    public int Version { get; set; }
    public string Kind { get; set; } = InstanceKinds.Automation;
    public string? ParentId { get; set; }
    /// <summary>For runs: which list they execute (<c>transition:ID</c>, <c>onEnter:ID</c>, <c>onExit:ID</c>, <c>sla:ID</c>); for sub-workflows, the parent step.</summary>
    public string? Reason { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string Status { get; set; } = InstanceStatus.Running;
    /// <summary>Record instances: the current state id.</summary>
    public string? State { get; set; }
    public DateTimeOffset? StateEnteredAt { get; set; }
    /// <summary>The interpreter's tokens, outputs and variables (JSON).</summary>
    public string Data { get; set; } = "{}";
    public string? TriggerJson { get; set; }
    public int Depth { get; set; }
    public string? InitiatorId { get; set; }
    public string? InitiatorName { get; set; }
    public string? Error { get; set; }
    public string? ErrorStepId { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long RowVersion { get; set; }
}

public static class StepStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

public sealed class StepRecord
{
    public string Id { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string? TokenId { get; set; }
    public int Attempt { get; set; } = 1;
    public string Status { get; set; } = StepStatus.Running;
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public static class BookmarkKinds
{
    public const string Timer = "timer";
    public const string Task = "task";
    public const string Signal = "signal";
    public const string Condition = "condition";
    public const string Child = "child";
    public const string Schedule = "schedule";
    public const string DateReached = "dateReached";
}

/// <summary>A durable pause point: a timer, a person, a signal or a condition.</summary>
public sealed class BookmarkRecord
{
    public string Id { get; set; } = "";
    public string? InstanceId { get; set; }
    public string? TokenId { get; set; }
    public string? NodeId { get; set; }
    public string Kind { get; set; } = BookmarkKinds.Timer;
    /// <summary>What happens when it fires: <c>wait</c>, <c>due</c>, <c>reminder</c>, <c>escalation</c>, <c>retry</c>, <c>timeout</c>, <c>auto:TRANSITION</c>, <c>sla:STATE</c>, <c>schedule</c>, <c>dateReached</c>.</summary>
    public string Purpose { get; set; } = "wait";
    public DateTimeOffset? DueAt { get; set; }
    public string? Signal { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? DefinitionId { get; set; }
    public string? TaskId { get; set; }
    public string? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Set while a worker processes it, so two workers never fire the same bookmark.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

public static class TaskStatus
{
    public const string Open = "open";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timedOut";
}

public sealed class TaskRecord
{
    public string Id { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string? RootInstanceId { get; set; }
    public string NodeId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string Kind { get; set; } = "approval";
    public string Title { get; set; } = "";
    public string? Instructions { get; set; }
    public string DefinitionKey { get; set; } = "";
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? EntityTitle { get; set; }
    public string Status { get; set; } = TaskStatus.Open;
    /// <summary>JSON array of user ids who may decide.</summary>
    public string Assignees { get; set; } = "[]";
    /// <summary>JSON: <c>{type, value}</c> for approvals.</summary>
    public string? Rule { get; set; }
    /// <summary>JSON array of <c>{key, label, tone}</c>.</summary>
    public string Outcomes { get; set; } = "[]";
    /// <summary>JSON array of decisions <c>{userId, name, outcome, comment, data, at}</c>.</summary>
    public string Decisions { get; set; } = "[]";
    public string? Form { get; set; }
    public string? RequireComment { get; set; }
    public bool AllowDelegation { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public int EscalationLevel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long RowVersion { get; set; }
}

public static class OutboxKinds
{
    public const string Change = "change";
    public const string Lifecycle = "lifecycle";
    public const string Notify = "notify";
    public const string Resume = "resume";
}

public sealed class OutboxRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = OutboxKinds.Change;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? Error { get; set; }
}

public sealed class AuditRecord
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? Tenant { get; set; }
    public string ActorType { get; set; } = "system";
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string SubjectType { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? InstanceId { get; set; }
    public string? DefinitionId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Data { get; set; }
    public string CorrelationId { get; set; } = "";
    public string? CausationId { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class SettingsRecord
{
    public string Key { get; set; } = "global";
    public string Json { get; set; } = "{\"version\":1}";
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
