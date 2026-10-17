namespace Zouriel.Workflows.Storage;

/// <summary>A record's registered field values. Relation and user fields hold ids.</summary>
public sealed class RecordSnapshot
{
    /// <summary>The registry entity key.</summary>
    public required string Entity { get; init; }
    /// <summary>The record's id (always text, whatever the key type).</summary>
    public required string Id { get; init; }
    /// <summary>Field key → workflow value (relations and users hold ids).</summary>
    public required Dictionary<string, object?> Values { get; init; }

    /// <summary>The value of a field, or null when it is not in the snapshot.</summary>
    public object? this[string field] => Values.TryGetValue(field, out var v) ? v : null;
}

/// <summary>Reads and writes the host app's records, by registry key (spec §2).</summary>
public interface IEntityGateway
{
    /// <summary>The record's current registered values, or null when it does not exist.</summary>
    Task<RecordSnapshot?> GetAsync(string entity, string id, CancellationToken ct = default);
    /// <summary>The related records of a <c>collection</c> field (used by <c>forEach</c>).</summary>
    Task<IReadOnlyList<RecordSnapshot>> GetCollectionAsync(string entity, string id, string field, CancellationToken ct = default);
    /// <summary>Writes fields on behalf of a workflow. The write is a committed change like any other (spec §7 setFields).</summary>
    Task SetFieldsAsync(string entity, string id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);
    /// <summary>
    /// Ids of records of an entity in key order, starting after <paramref name="after"/> (null = from the
    /// start), at most <paramref name="limit"/>. Callers page through all records by passing the last id back.
    /// </summary>
    Task<IReadOnlyList<string>> ListIdsAsync(string entity, string? after, int limit, CancellationToken ct = default);

    /// <summary>
    /// Creates a record on behalf of a workflow (spec §7 createRecord) and returns its id. The write is a
    /// committed change like any other. Gateways that cannot create records refuse with <c>notSupported</c>.
    /// </summary>
    Task<string> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default) =>
        throw new WorkflowException("notSupported", $"This app does not let workflows create {entity} records.");

    /// <summary>Deletes a record (a reset undoing a record a workflow created). Gateways that cannot refuse with <c>notSupported</c>.</summary>
    Task DeleteAsync(string entity, string id, CancellationToken ct = default) =>
        throw new WorkflowException("notSupported", $"This app does not let workflows delete {entity} records.");
}

/// <summary>Filters for listing instances.</summary>
public sealed class InstanceQuery
{
    /// <summary>Only instances of this definition.</summary>
    public string? DefinitionId { get; init; }
    /// <summary>Only instances of the definition with this key.</summary>
    public string? DefinitionKey { get; init; }
    /// <summary>Only instances about records of this entity.</summary>
    public string? Entity { get; init; }
    /// <summary>Only instances about this record id.</summary>
    public string? EntityId { get; init; }
    /// <summary>Only these statuses (null = any).</summary>
    public IReadOnlyList<string>? Statuses { get; init; }
    /// <summary>Only these kinds (null = any).</summary>
    public IReadOnlyList<string>? Kinds { get; init; }
    /// <summary>Only children of this instance.</summary>
    public string? ParentId { get; init; }
    /// <summary>Rows to skip (paging).</summary>
    public int Skip { get; init; }
    /// <summary>Rows to return at most (newest first).</summary>
    public int Take { get; init; } = 50;
}

/// <summary>Filters for listing tasks.</summary>
public sealed class TaskQuery
{
    /// <summary>Only tasks this user is assigned to.</summary>
    public string? AssigneeId { get; init; }
    /// <summary>Only tasks of this instance.</summary>
    public string? InstanceId { get; init; }
    /// <summary>Only tasks about records of this entity.</summary>
    public string? Entity { get; init; }
    /// <summary>Only tasks about this record id.</summary>
    public string? EntityId { get; init; }
    /// <summary>Only these statuses (null = any).</summary>
    public IReadOnlyList<string>? Statuses { get; init; }
    /// <summary>Rows to skip (paging).</summary>
    public int Skip { get; init; }
    /// <summary>Rows to return at most (open first, then by due time).</summary>
    public int Take { get; init; } = 50;
}

/// <summary>Filters for reading the audit log.</summary>
public sealed record AuditQuery
{
    /// <summary>
    /// The audit chain to read (<see cref="AuditRecord.Tenant"/>; null = the default chain). Stores return only this
    /// chain's entries, so apps sharing one audit table never see each other's. The engine sets it to its own tenant.
    /// </summary>
    public string? Tenant { get; init; }
    /// <summary>Only this category.</summary>
    public string? Category { get; init; }
    /// <summary>Only this action.</summary>
    public string? Action { get; init; }
    /// <summary>Only entries about records of this entity.</summary>
    public string? Entity { get; init; }
    /// <summary>Only entries about this record id.</summary>
    public string? EntityId { get; init; }
    /// <summary>Only entries of this instance (or correlated with it).</summary>
    public string? InstanceId { get; init; }
    /// <summary>Only entries about this definition.</summary>
    public string? DefinitionId { get; init; }
    /// <summary>Only entries by this actor.</summary>
    public string? ActorId { get; init; }
    /// <summary>Only entries at or after this instant.</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Only entries before this instant.</summary>
    public DateTimeOffset? To { get; init; }
    /// <summary>Return entries with <c>Seq</c> below this (newest-first paging).</summary>
    public long? Before { get; init; }
    /// <summary>Return entries with <c>Seq</c> at or above this, oldest first (for exports and verification).</summary>
    public long? FromSeq { get; init; }
    /// <summary>Rows to return at most.</summary>
    public int Take { get; init; } = 100;
}

/// <summary>Opens units of work. Everything inside one session commits or rolls back together.</summary>
public interface IWorkflowStore
{
    /// <summary>Opens a unit of work; dispose it without committing to roll everything back.</summary>
    Task<IWorkflowSession> BeginAsync(CancellationToken ct = default);
}

/// <summary>
/// One unit of work. Engines write workflow state, audit and outbox rows through it; with EF Core the
/// host's business changes made through <see cref="Entities"/> commit in the same transaction.
/// </summary>
public interface IWorkflowSession : IAsyncDisposable
{
    /// <summary>The host's records, read and written inside this unit.</summary>
    IEntityGateway Entities { get; }

    /// <summary>The DI scope of this unit of work, if any; app services the engine calls are resolved from it.</summary>
    IServiceProvider? Services => null;

    /// <summary>A definition by id, or null.</summary>
    Task<DefinitionRecord?> GetDefinitionAsync(string id, CancellationToken ct = default);
    /// <summary>A definition by key, or null.</summary>
    Task<DefinitionRecord?> GetDefinitionByKeyAsync(string key, CancellationToken ct = default);
    /// <summary>Every definition.</summary>
    Task<IReadOnlyList<DefinitionRecord>> ListDefinitionsAsync(CancellationToken ct = default);
    /// <summary>Adds a new definition.</summary>
    Task AddDefinitionAsync(DefinitionRecord d, CancellationToken ct = default);
    /// <summary>Saves changes to a definition (bumping its row version).</summary>
    Task UpdateDefinitionAsync(DefinitionRecord d, CancellationToken ct = default);
    /// <summary>Deletes a definition that was never published.</summary>
    Task DeleteDefinitionAsync(string id, CancellationToken ct = default);

    /// <summary>One published version, or null.</summary>
    Task<DefinitionVersionRecord?> GetVersionAsync(string definitionId, int version, CancellationToken ct = default);
    /// <summary>All published versions of a definition, oldest first.</summary>
    Task<IReadOnlyList<DefinitionVersionRecord>> ListVersionsAsync(string definitionId, CancellationToken ct = default);
    /// <summary>Stores a newly published version.</summary>
    Task AddVersionAsync(DefinitionVersionRecord v, CancellationToken ct = default);

    /// <summary>Every live trigger index row.</summary>
    Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(CancellationToken ct = default);
    /// <summary>Replaces a definition's trigger rows (on publish, archive and restore).</summary>
    Task ReplaceTriggersAsync(string definitionId, IReadOnlyList<TriggerRecord> triggers, CancellationToken ct = default);
    /// <summary>Records that a change event started (or was considered by) a trigger. False when it already did.</summary>
    Task<bool> TryClaimTriggerEventAsync(string definitionId, string eventId, CancellationToken ct = default);

    /// <summary>An instance by id, or null.</summary>
    Task<InstanceRecord?> GetInstanceAsync(string id, CancellationToken ct = default);
    /// <summary>Instances matching a query, newest first.</summary>
    Task<IReadOnlyList<InstanceRecord>> QueryInstancesAsync(InstanceQuery q, CancellationToken ct = default);
    /// <summary>How many instances match a query.</summary>
    Task<int> CountInstancesAsync(InstanceQuery q, CancellationToken ct = default);
    /// <summary>How many instances (automation and record kinds) each version of a definition has.</summary>
    Task<IReadOnlyDictionary<int, int>> CountInstancesByVersionAsync(string definitionId, CancellationToken ct = default);
    /// <summary>Adds a new instance.</summary>
    Task AddInstanceAsync(InstanceRecord i, CancellationToken ct = default);
    /// <summary>Saves an instance; refused (at commit) when another unit changed it since it was read.</summary>
    Task UpdateInstanceAsync(InstanceRecord i, CancellationToken ct = default);

    /// <summary>Adds a step execution record.</summary>
    Task AddStepAsync(StepRecord s, CancellationToken ct = default);
    /// <summary>Saves a step execution record.</summary>
    Task UpdateStepAsync(StepRecord s, CancellationToken ct = default);
    /// <summary>An instance's step records, oldest first.</summary>
    Task<IReadOnlyList<StepRecord>> ListStepsAsync(string instanceId, CancellationToken ct = default);

    /// <summary>Adds a bookmark.</summary>
    Task AddBookmarkAsync(BookmarkRecord b, CancellationToken ct = default);
    /// <summary>Removes a bookmark (no error when it is already gone).</summary>
    Task RemoveBookmarkAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// Saves changes to a bookmark read in this unit (failed attempts, backoff, parking). Stores that save the rows they
    /// track need not implement it; the default does nothing.
    /// </summary>
    Task UpdateBookmarkAsync(BookmarkRecord b, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>A bookmark by id, or null once removed.</summary>
    Task<BookmarkRecord?> GetBookmarkAsync(string id, CancellationToken ct = default);
    /// <summary>An instance's bookmarks.</summary>
    Task<IReadOnlyList<BookmarkRecord>> ListBookmarksAsync(string instanceId, CancellationToken ct = default);
    /// <summary>Due timers, claimed until <paramref name="lockUntil"/> so other workers skip them.</summary>
    Task<IReadOnlyList<BookmarkRecord>> ClaimDueBookmarksAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default);
    /// <summary>
    /// Bookmarks of a kind about one record. <paramref name="entity"/> and <paramref name="entityId"/> match
    /// exactly: null matches only bookmarks that are about no record. <paramref name="signal"/> null matches any signal.
    /// </summary>
    Task<IReadOnlyList<BookmarkRecord>> FindBookmarksAsync(string kind, string? entity, string? entityId, string? signal, CancellationToken ct = default);
    /// <summary>A definition's trigger bookmarks of a kind (schedule, dateReached; not instance ones).</summary>
    Task<IReadOnlyList<BookmarkRecord>> FindDefinitionBookmarksAsync(string definitionId, string kind, CancellationToken ct = default);

    /// <summary>A task by id, or null.</summary>
    Task<TaskRecord?> GetTaskAsync(string id, CancellationToken ct = default);
    /// <summary>Tasks matching a query (open first, then by due time).</summary>
    Task<IReadOnlyList<TaskRecord>> QueryTasksAsync(TaskQuery q, CancellationToken ct = default);
    /// <summary>How many tasks match a query.</summary>
    Task<int> CountTasksAsync(TaskQuery q, CancellationToken ct = default);
    /// <summary>Adds a new task.</summary>
    Task AddTaskAsync(TaskRecord t, CancellationToken ct = default);
    /// <summary>Saves a task; refused (at commit) when another unit changed it since it was read.</summary>
    Task UpdateTaskAsync(TaskRecord t, CancellationToken ct = default);

    /// <summary>Adds an outbox row, delivered after this unit commits.</summary>
    Task AddOutboxAsync(OutboxRecord o, CancellationToken ct = default);
    /// <summary>Pending outbox rows, oldest first, claimed until <paramref name="lockUntil"/> so other workers skip them.</summary>
    Task<IReadOnlyList<OutboxRecord>> ClaimOutboxAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default);
    /// <summary>Saves an outbox row (processed, failed or re-locked).</summary>
    Task UpdateOutboxAsync(OutboxRecord o, CancellationToken ct = default);
    /// <summary>
    /// Whether an unprocessed outbox row of the same <see cref="OutboxRecord.RecordKey"/> was written before
    /// <paramref name="row"/> (earlier <c>CreatedAt</c>, or the same and a smaller <c>Id</c>), whatever its lock: the
    /// engine then leaves <paramref name="row"/> for later, so one record's events are processed in order. Stores
    /// should implement it (indexed on RecordKey, ProcessedAt); the default answers false (no ordering).
    /// </summary>
    Task<bool> HasEarlierPendingOutboxAsync(OutboxRecord row, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>
    /// The head of a tenant's audit chain (null before the first entry), locked for the rest of the unit so
    /// concurrent writers form one chain. The engine calls it once per unit, right before committing.
    /// </summary>
    Task<AuditHead?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default);
    /// <summary>Moves the head of a tenant's audit chain (after adding the entries that lead to it).</summary>
    Task SetAuditHeadAsync(string? tenant, AuditHead head, CancellationToken ct = default);
    /// <summary>Stores an audit entry already linked into the chain.</summary>
    Task AddAuditAsync(AuditRecord a, CancellationToken ct = default);
    /// <summary>Audit entries matching a query (newest first, or oldest first with <c>FromSeq</c>).</summary>
    Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default);
    /// <summary>
    /// Deletes the entries of a category older than <paramref name="before"/> in the audit chain of
    /// <paramref name="tenant"/> (null = the default chain; other chains are never touched) and reports exactly which
    /// sequence numbers went.
    /// </summary>
    Task<AuditPurgeResult> PurgeAuditAsync(string? tenant, string category, DateTimeOffset before, CancellationToken ct = default);

    /// <summary>Deletes housekeeping rows: processed outbox rows and trigger claims older than the given instants.</summary>
    Task<int> CleanupAsync(DateTimeOffset processedOutboxBefore, DateTimeOffset claimsBefore, CancellationToken ct = default);

    /// <summary>
    /// Saves record changes made in this unit but not saved yet (an app action that changed entities through the host's
    /// own data context without saving them). The engine calls it inside the action's write scope right after the
    /// handler returns, so the change hook sees those writes as the workflow's (actor, depth, causation) rather than
    /// as anonymous writes at commit. Stores that write records at once need nothing; the default does nothing.
    /// </summary>
    Task SaveRecordChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Starts a savepoint inside the unit (used around app actions so a failing handler's writes are undone).
    /// Returns false when the store cannot do it; the engine then carries on without one.
    /// </summary>
    Task<bool> CreateSavepointAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Undoes everything written since the savepoint, including changes tracked but not yet saved.</summary>
    Task RollbackToSavepointAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Forgets a savepoint whose work is kept.</summary>
    Task ReleaseSavepointAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>The stored global settings, or null when never saved.</summary>
    Task<SettingsRecord?> GetSettingsAsync(CancellationToken ct = default);
    /// <summary>Stores the global settings.</summary>
    Task SaveSettingsAsync(SettingsRecord s, CancellationToken ct = default);

    /// <summary>Commits everything written in this unit, or throws <c>WorkflowConcurrencyException</c> on a conflict.</summary>
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
