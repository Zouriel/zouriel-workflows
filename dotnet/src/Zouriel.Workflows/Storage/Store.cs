namespace Zouriel.Workflows.Storage;

/// <summary>A record's registered field values. Relation and user fields hold ids.</summary>
public sealed class RecordSnapshot
{
    public required string Entity { get; init; }
    public required string Id { get; init; }
    public required Dictionary<string, object?> Values { get; init; }

    public object? this[string field] => Values.TryGetValue(field, out var v) ? v : null;
}

/// <summary>Reads and writes the host app's records, by registry key (spec §2).</summary>
public interface IEntityGateway
{
    Task<RecordSnapshot?> GetAsync(string entity, string id, CancellationToken ct = default);
    Task<IReadOnlyList<RecordSnapshot>> GetCollectionAsync(string entity, string id, string field, CancellationToken ct = default);
    /// <summary>Writes fields on behalf of a workflow. The write is a committed change like any other (spec §7 setFields).</summary>
    Task SetFieldsAsync(string entity, string id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);
    /// <summary>Ids of records of an entity, newest first, for scheduled runs over many records.</summary>
    Task<IReadOnlyList<string>> ListIdsAsync(string entity, int limit, CancellationToken ct = default);
}

public sealed class InstanceQuery
{
    public string? DefinitionId { get; init; }
    public string? DefinitionKey { get; init; }
    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    public IReadOnlyList<string>? Statuses { get; init; }
    public IReadOnlyList<string>? Kinds { get; init; }
    public string? ParentId { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

public sealed class TaskQuery
{
    public string? AssigneeId { get; init; }
    public IReadOnlyList<string>? AssigneeAnyOf { get; init; }
    public string? InstanceId { get; init; }
    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    public IReadOnlyList<string>? Statuses { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

public sealed class AuditQuery
{
    public string? Category { get; init; }
    public string? Action { get; init; }
    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    public string? InstanceId { get; init; }
    public string? DefinitionId { get; init; }
    public string? ActorId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>Return entries with <c>Seq</c> below this (newest-first paging).</summary>
    public long? Before { get; init; }
    /// <summary>Return entries with <c>Seq</c> at or above this, oldest first (for exports and verification).</summary>
    public long? FromSeq { get; init; }
    public int Take { get; init; } = 100;
}

/// <summary>Opens units of work. Everything inside one session commits or rolls back together.</summary>
public interface IWorkflowStore
{
    Task<IWorkflowSession> BeginAsync(CancellationToken ct = default);
}

/// <summary>
/// One unit of work. Engines write workflow state, audit and outbox rows through it; with EF Core the
/// host's business changes made through <see cref="Entities"/> commit in the same transaction.
/// </summary>
public interface IWorkflowSession : IAsyncDisposable
{
    IEntityGateway Entities { get; }

    Task<DefinitionRecord?> GetDefinitionAsync(string id, CancellationToken ct = default);
    Task<DefinitionRecord?> GetDefinitionByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<DefinitionRecord>> ListDefinitionsAsync(CancellationToken ct = default);
    Task AddDefinitionAsync(DefinitionRecord d, CancellationToken ct = default);
    Task UpdateDefinitionAsync(DefinitionRecord d, CancellationToken ct = default);
    Task DeleteDefinitionAsync(string id, CancellationToken ct = default);

    Task<DefinitionVersionRecord?> GetVersionAsync(string definitionId, int version, CancellationToken ct = default);
    Task<IReadOnlyList<DefinitionVersionRecord>> ListVersionsAsync(string definitionId, CancellationToken ct = default);
    Task AddVersionAsync(DefinitionVersionRecord v, CancellationToken ct = default);

    Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(CancellationToken ct = default);
    Task ReplaceTriggersAsync(string definitionId, IReadOnlyList<TriggerRecord> triggers, CancellationToken ct = default);
    /// <summary>Records that a change event started (or was considered by) a trigger. False when it already did.</summary>
    Task<bool> TryClaimTriggerEventAsync(string definitionId, string eventId, CancellationToken ct = default);

    Task<InstanceRecord?> GetInstanceAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<InstanceRecord>> QueryInstancesAsync(InstanceQuery q, CancellationToken ct = default);
    Task<int> CountInstancesAsync(InstanceQuery q, CancellationToken ct = default);
    Task AddInstanceAsync(InstanceRecord i, CancellationToken ct = default);
    Task UpdateInstanceAsync(InstanceRecord i, CancellationToken ct = default);

    Task AddStepAsync(StepRecord s, CancellationToken ct = default);
    Task UpdateStepAsync(StepRecord s, CancellationToken ct = default);
    Task<IReadOnlyList<StepRecord>> ListStepsAsync(string instanceId, CancellationToken ct = default);

    Task AddBookmarkAsync(BookmarkRecord b, CancellationToken ct = default);
    Task RemoveBookmarkAsync(string id, CancellationToken ct = default);
    Task<BookmarkRecord?> GetBookmarkAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<BookmarkRecord>> ListBookmarksAsync(string instanceId, CancellationToken ct = default);
    /// <summary>Due timers, claimed until <paramref name="lockUntil"/> so other workers skip them.</summary>
    Task<IReadOnlyList<BookmarkRecord>> ClaimDueBookmarksAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<BookmarkRecord>> FindBookmarksAsync(string kind, string? entity, string? entityId, string? signal, CancellationToken ct = default);
    Task<IReadOnlyList<BookmarkRecord>> FindDefinitionBookmarksAsync(string definitionId, string kind, CancellationToken ct = default);

    Task<TaskRecord?> GetTaskAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<TaskRecord>> QueryTasksAsync(TaskQuery q, CancellationToken ct = default);
    Task<int> CountTasksAsync(TaskQuery q, CancellationToken ct = default);
    Task AddTaskAsync(TaskRecord t, CancellationToken ct = default);
    Task UpdateTaskAsync(TaskRecord t, CancellationToken ct = default);

    Task AddOutboxAsync(OutboxRecord o, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxRecord>> ClaimOutboxAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default);
    Task UpdateOutboxAsync(OutboxRecord o, CancellationToken ct = default);

    /// <summary>The newest audit entry, read with a lock so concurrent writers form one chain.</summary>
    Task<AuditRecord?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default);
    Task AddAuditAsync(AuditRecord a, CancellationToken ct = default);
    Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default);
    Task<int> PurgeAuditAsync(string category, DateTimeOffset before, CancellationToken ct = default);

    Task<SettingsRecord?> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(SettingsRecord s, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);
}
