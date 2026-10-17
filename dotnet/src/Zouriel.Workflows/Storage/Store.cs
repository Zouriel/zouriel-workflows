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
    /// <summary>
    /// Ids of records of an entity in key order, starting after <paramref name="after"/> (null = from the
    /// start), at most <paramref name="limit"/>. Callers page through all records by passing the last id back.
    /// </summary>
    Task<IReadOnlyList<string>> ListIdsAsync(string entity, string? after, int limit, CancellationToken ct = default);
}

/// <summary>Filters for listing instances.</summary>
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

/// <summary>Filters for listing tasks.</summary>
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

/// <summary>Filters for reading the audit log.</summary>
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

    /// <summary>The DI scope of this unit of work, if any; app services the engine calls are resolved from it.</summary>
    IServiceProvider? Services => null;

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
    /// <summary>How many instances (automation and record kinds) each version of a definition has.</summary>
    Task<IReadOnlyDictionary<int, int>> CountInstancesByVersionAsync(string definitionId, CancellationToken ct = default);
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
    /// <summary>
    /// Bookmarks of a kind about one record. <paramref name="entity"/> and <paramref name="entityId"/> match
    /// exactly: null matches only bookmarks that are about no record. <paramref name="signal"/> null matches any signal.
    /// </summary>
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

    /// <summary>
    /// The head of a tenant's audit chain (null before the first entry), locked for the rest of the unit so
    /// concurrent writers form one chain. The engine calls it once per unit, right before committing.
    /// </summary>
    Task<AuditHead?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default);
    /// <summary>Moves the head of a tenant's audit chain (after adding the entries that lead to it).</summary>
    Task SetAuditHeadAsync(string? tenant, AuditHead head, CancellationToken ct = default);
    Task AddAuditAsync(AuditRecord a, CancellationToken ct = default);
    Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default);
    /// <summary>Deletes the entries of a category older than <paramref name="before"/> and reports exactly which sequence numbers went.</summary>
    Task<AuditPurgeResult> PurgeAuditAsync(string category, DateTimeOffset before, CancellationToken ct = default);

    /// <summary>Deletes housekeeping rows: processed outbox rows and trigger claims older than the given instants.</summary>
    Task<int> CleanupAsync(DateTimeOffset processedOutboxBefore, DateTimeOffset claimsBefore, CancellationToken ct = default);

    /// <summary>
    /// Starts a savepoint inside the unit (used around app actions so a failing handler's writes are undone).
    /// Returns false when the store cannot do it; the engine then carries on without one.
    /// </summary>
    Task<bool> CreateSavepointAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Undoes everything written since the savepoint, including changes tracked but not yet saved.</summary>
    Task RollbackToSavepointAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Forgets a savepoint whose work is kept.</summary>
    Task ReleaseSavepointAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    Task<SettingsRecord?> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(SettingsRecord s, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);
}

/// <summary>The newest entry of an audit chain: its sequence number and hash.</summary>
public sealed record AuditHead(long Seq, string Hash);

/// <summary>An inclusive run of audit sequence numbers.</summary>
public readonly record struct SeqRange(long From, long To)
{
    /// <summary>Turns ascending sequence numbers into the fewest inclusive runs.</summary>
    public static List<SeqRange> Compress(IEnumerable<long> ascending)
    {
        var list = new List<SeqRange>();
        foreach (var s in ascending)
        {
            if (list.Count > 0 && list[^1].To + 1 == s) list[^1] = list[^1] with { To = s };
            else if (list.Count > 0 && s <= list[^1].To) continue;
            else list.Add(new SeqRange(s, s));
        }
        return list;
    }
}

/// <summary>What a purge deleted: how many entries, and exactly which sequence numbers.</summary>
public sealed record AuditPurgeResult(int Count, IReadOnlyList<SeqRange> Ranges);
