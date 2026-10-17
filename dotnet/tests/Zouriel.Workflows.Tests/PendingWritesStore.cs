using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Tests;

/// <summary>
/// A store whose sessions hold record writes back until they are saved, like an EF Core data context: writes queued
/// with <see cref="PendingSession.Defer"/> run on <c>SaveRecordChangesAsync</c>, or at commit when nobody saved them.
/// </summary>
internal sealed class PendingWritesStore(IWorkflowStore inner) : IWorkflowStore
{
    public static PendingSession? Current { get; private set; }

    public async Task<IWorkflowSession> BeginAsync(CancellationToken ct = default) => Current = new PendingSession(await inner.BeginAsync(ct));
}

internal sealed class PendingSession(IWorkflowSession inner) : IWorkflowSession
{
    private readonly List<Func<Task>> _pending = [];

    /// <summary>How many of the next trigger listings fail (to make processing a change event fail once).</summary>
    public static int FailTriggerListings;

    /// <summary>Queues a record write as if made on a data context and not saved yet.</summary>
    public void Defer(Func<IEntityGateway, Task> write) => _pending.Add(() => write(inner.Entities));

    public IEntityGateway Entities => inner.Entities;
    public IServiceProvider? Services => inner.Services;

    public async Task SaveRecordChangesAsync(CancellationToken ct = default)
    {
        var writes = _pending.ToList();
        _pending.Clear();
        foreach (var w in writes) await w();
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await SaveRecordChangesAsync(ct);
        await inner.CommitAsync(ct);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public Task<DefinitionRecord?> GetDefinitionAsync(string id, CancellationToken ct = default) => inner.GetDefinitionAsync(id, ct);
    public Task<DefinitionRecord?> GetDefinitionByKeyAsync(string key, CancellationToken ct = default) => inner.GetDefinitionByKeyAsync(key, ct);
    public Task<IReadOnlyList<DefinitionRecord>> ListDefinitionsAsync(CancellationToken ct = default) => inner.ListDefinitionsAsync(ct);
    public Task AddDefinitionAsync(DefinitionRecord d, CancellationToken ct = default) => inner.AddDefinitionAsync(d, ct);
    public Task UpdateDefinitionAsync(DefinitionRecord d, CancellationToken ct = default) => inner.UpdateDefinitionAsync(d, ct);
    public Task DeleteDefinitionAsync(string id, CancellationToken ct = default) => inner.DeleteDefinitionAsync(id, ct);
    public Task<DefinitionVersionRecord?> GetVersionAsync(string definitionId, int version, CancellationToken ct = default) => inner.GetVersionAsync(definitionId, version, ct);
    public Task<IReadOnlyList<DefinitionVersionRecord>> ListVersionsAsync(string definitionId, CancellationToken ct = default) => inner.ListVersionsAsync(definitionId, ct);
    public Task AddVersionAsync(DefinitionVersionRecord v, CancellationToken ct = default) => inner.AddVersionAsync(v, ct);
    public Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(CancellationToken ct = default) =>
        Interlocked.Decrement(ref FailTriggerListings) >= 0 ? throw new InvalidOperationException("The database is unavailable.") : inner.ListTriggersAsync(ct);
    public Task ReplaceTriggersAsync(string definitionId, IReadOnlyList<TriggerRecord> triggers, CancellationToken ct = default) => inner.ReplaceTriggersAsync(definitionId, triggers, ct);
    public Task<bool> TryClaimTriggerEventAsync(string definitionId, string eventId, CancellationToken ct = default) => inner.TryClaimTriggerEventAsync(definitionId, eventId, ct);
    public Task<InstanceRecord?> GetInstanceAsync(string id, CancellationToken ct = default) => inner.GetInstanceAsync(id, ct);
    public Task<IReadOnlyList<InstanceRecord>> QueryInstancesAsync(InstanceQuery q, CancellationToken ct = default) => inner.QueryInstancesAsync(q, ct);
    public Task<int> CountInstancesAsync(InstanceQuery q, CancellationToken ct = default) => inner.CountInstancesAsync(q, ct);
    public Task<IReadOnlyDictionary<int, int>> CountInstancesByVersionAsync(string definitionId, CancellationToken ct = default) => inner.CountInstancesByVersionAsync(definitionId, ct);
    public Task AddInstanceAsync(InstanceRecord i, CancellationToken ct = default) => inner.AddInstanceAsync(i, ct);
    public Task UpdateInstanceAsync(InstanceRecord i, CancellationToken ct = default) => inner.UpdateInstanceAsync(i, ct);
    public Task AddStepAsync(StepRecord s, CancellationToken ct = default) => inner.AddStepAsync(s, ct);
    public Task UpdateStepAsync(StepRecord s, CancellationToken ct = default) => inner.UpdateStepAsync(s, ct);
    public Task<IReadOnlyList<StepRecord>> ListStepsAsync(string instanceId, CancellationToken ct = default) => inner.ListStepsAsync(instanceId, ct);
    public Task AddBookmarkAsync(BookmarkRecord b, CancellationToken ct = default) => inner.AddBookmarkAsync(b, ct);
    public Task RemoveBookmarkAsync(string id, CancellationToken ct = default) => inner.RemoveBookmarkAsync(id, ct);
    public Task UpdateBookmarkAsync(BookmarkRecord b, CancellationToken ct = default) => inner.UpdateBookmarkAsync(b, ct);
    public Task<BookmarkRecord?> GetBookmarkAsync(string id, CancellationToken ct = default) => inner.GetBookmarkAsync(id, ct);
    public Task<IReadOnlyList<BookmarkRecord>> ListBookmarksAsync(string instanceId, CancellationToken ct = default) => inner.ListBookmarksAsync(instanceId, ct);
    public Task<IReadOnlyList<BookmarkRecord>> ClaimDueBookmarksAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default) => inner.ClaimDueBookmarksAsync(now, lockUntil, limit, ct);
    public Task<IReadOnlyList<BookmarkRecord>> FindBookmarksAsync(string kind, string? entity, string? entityId, string? signal, CancellationToken ct = default) => inner.FindBookmarksAsync(kind, entity, entityId, signal, ct);
    public Task<IReadOnlyList<BookmarkRecord>> FindDefinitionBookmarksAsync(string definitionId, string kind, CancellationToken ct = default) => inner.FindDefinitionBookmarksAsync(definitionId, kind, ct);
    public Task<TaskRecord?> GetTaskAsync(string id, CancellationToken ct = default) => inner.GetTaskAsync(id, ct);
    public Task<IReadOnlyList<TaskRecord>> QueryTasksAsync(TaskQuery q, CancellationToken ct = default) => inner.QueryTasksAsync(q, ct);
    public Task<int> CountTasksAsync(TaskQuery q, CancellationToken ct = default) => inner.CountTasksAsync(q, ct);
    public Task AddTaskAsync(TaskRecord t, CancellationToken ct = default) => inner.AddTaskAsync(t, ct);
    public Task UpdateTaskAsync(TaskRecord t, CancellationToken ct = default) => inner.UpdateTaskAsync(t, ct);
    public Task AddOutboxAsync(OutboxRecord o, CancellationToken ct = default) => inner.AddOutboxAsync(o, ct);
    public Task<IReadOnlyList<OutboxRecord>> ClaimOutboxAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default) => inner.ClaimOutboxAsync(now, lockUntil, limit, ct);
    public Task UpdateOutboxAsync(OutboxRecord o, CancellationToken ct = default) => inner.UpdateOutboxAsync(o, ct);
    public Task<bool> HasEarlierPendingOutboxAsync(OutboxRecord row, CancellationToken ct = default) => inner.HasEarlierPendingOutboxAsync(row, ct);
    public Task<AuditHead?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default) => inner.GetAuditHeadAsync(tenant, ct);
    public Task SetAuditHeadAsync(string? tenant, AuditHead head, CancellationToken ct = default) => inner.SetAuditHeadAsync(tenant, head, ct);
    public Task AddAuditAsync(AuditRecord a, CancellationToken ct = default) => inner.AddAuditAsync(a, ct);
    public Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default) => inner.QueryAuditAsync(q, ct);
    public Task<AuditPurgeResult> PurgeAuditAsync(string? tenant, string category, DateTimeOffset before, CancellationToken ct = default) => inner.PurgeAuditAsync(tenant, category, before, ct);
    public Task<int> CleanupAsync(DateTimeOffset processedOutboxBefore, DateTimeOffset claimsBefore, CancellationToken ct = default) => inner.CleanupAsync(processedOutboxBefore, claimsBefore, ct);
    public Task<bool> CreateSavepointAsync(string name, CancellationToken ct = default) => inner.CreateSavepointAsync(name, ct);
    public Task RollbackToSavepointAsync(string name, CancellationToken ct = default) => inner.RollbackToSavepointAsync(name, ct);
    public Task ReleaseSavepointAsync(string name, CancellationToken ct = default) => inner.ReleaseSavepointAsync(name, ct);
    public Task<SettingsRecord?> GetSettingsAsync(CancellationToken ct = default) => inner.GetSettingsAsync(ct);
    public Task SaveSettingsAsync(SettingsRecord s, CancellationToken ct = default) => inner.SaveSettingsAsync(s, ct);
}
