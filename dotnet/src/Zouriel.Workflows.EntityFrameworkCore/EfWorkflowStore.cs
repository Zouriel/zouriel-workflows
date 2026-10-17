using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zouriel.Workflows.DependencyInjection;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore;

/// <summary>
/// Stores workflow state in the host's DbContext. Each unit of work gets its own DI scope, DbContext
/// and database transaction; record writes made by workflows go through the same context, so they
/// commit (or roll back) with the workflow state.
/// </summary>
/// <remarks>
/// Row claims (timers, outbox) and the audit chain head are locked with <c>FOR UPDATE SKIP LOCKED</c> on
/// PostgreSQL and <c>UPDLOCK, READPAST</c> on SQL Server. SQLite serialises writers itself. Other providers
/// fall back to unlocked queries (a warning is logged once): run a single worker there, or two workers
/// may claim the same row and the audit chain may fork.
/// </remarks>
/// <typeparam name="TContext">The app's DbContext, which must call <c>modelBuilder.AddWorkflows()</c>.</typeparam>
public sealed class EfWorkflowStore<TContext>(IServiceScopeFactory scopes, WorkflowRegistry registry) : IWorkflowStore where TContext : DbContext
{
    private static int _warnedUnknownProvider;

    /// <summary>Opens a unit of work: a DI scope, its DbContext and a database transaction.</summary>
    public async Task<IWorkflowSession> BeginAsync(CancellationToken ct = default)
    {
        var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        if (EfWorkflowSession.ProviderOf(db) == EfWorkflowSession.Provider.Other && Interlocked.Exchange(ref _warnedUnknownProvider, 1) == 0)
        {
            const string text = "Zouriel.Workflows does not know how to lock rows on this database provider; claims and the audit chain use unlocked queries. Run a single workflow worker.";
            if (scope.ServiceProvider.GetService<ILoggerFactory>() is { } logs)
                logs.CreateLogger("Zouriel.Workflows.EntityFrameworkCore").LogWarning("{Text} Provider: {Provider}", text, db.Database.ProviderName);
            else System.Diagnostics.Trace.TraceWarning($"{text} Provider: {db.Database.ProviderName}");
        }
        var time = scope.ServiceProvider.GetService<WorkflowEngineSettings>()?.Time ?? TimeProvider.System;
        var tx = await DbErrors.MapAsync(() => db.Database.BeginTransactionAsync(ct));
        return new EfWorkflowSession(db, tx, scope, registry, time);
    }
}

internal sealed class EfWorkflowSession(DbContext db, IDbContextTransaction tx, AsyncServiceScope scope, WorkflowRegistry registry, TimeProvider time) : IWorkflowSession
{
    private bool _committed;
    private readonly Provider _provider = ProviderOf(db);
    private readonly Dictionary<string, HashSet<object>> _savepoints = [];

    public IEntityGateway Entities { get; } = new EfEntityGateway(db, registry);
    public IServiceProvider Services => scope.ServiceProvider;
    internal DbContext Db => db;

    internal enum Provider { Postgres, SqlServer, Sqlite, Other }

    internal static Provider ProviderOf(DbContext db) => db.Database.ProviderName switch
    {
        { } n when n.Contains("Npgsql", StringComparison.Ordinal) => Provider.Postgres,
        { } n when n.Contains("SqlServer", StringComparison.Ordinal) => Provider.SqlServer,
        { } n when n.Contains("Sqlite", StringComparison.Ordinal) => Provider.Sqlite,
        _ => Provider.Other,
    };

    /// <summary>Queries go to the database, so pending changes are written (inside the transaction) first.</summary>
    private async Task FlushAsync(CancellationToken ct)
    {
        if (!db.ChangeTracker.HasChanges()) return;
        await DbErrors.MapAsync(() => db.SaveChangesAsync(ct));
    }

    private DbSet<T> Set<T>() where T : class => db.Set<T>();

    private void Upsert<T>(T entity) where T : class
    {
        var entry = db.Entry(entity);
        if (entry.State == EntityState.Detached) db.Update(entity);
    }

    // ── Definitions ───────────────────────────────────────────────────────

    public async Task<DefinitionRecord?> GetDefinitionAsync(string id, CancellationToken ct = default) { await FlushAsync(ct); return await Set<DefinitionRecord>().FirstOrDefaultAsync(d => d.Id == id, ct); }
    public async Task<DefinitionRecord?> GetDefinitionByKeyAsync(string key, CancellationToken ct = default) { await FlushAsync(ct); return await Set<DefinitionRecord>().FirstOrDefaultAsync(d => d.Key == key, ct); }
    public async Task<IReadOnlyList<DefinitionRecord>> ListDefinitionsAsync(CancellationToken ct = default) { await FlushAsync(ct); return await Set<DefinitionRecord>().ToListAsync(ct); }
    public Task AddDefinitionAsync(DefinitionRecord d, CancellationToken ct = default) { Set<DefinitionRecord>().Add(d); return Task.CompletedTask; }
    public Task UpdateDefinitionAsync(DefinitionRecord d, CancellationToken ct = default) { d.RowVersion++; Upsert(d); return Task.CompletedTask; }
    public async Task DeleteDefinitionAsync(string id, CancellationToken ct = default)
    {
        var d = await GetDefinitionAsync(id, ct);
        if (d is not null) Set<DefinitionRecord>().Remove(d);
    }

    public async Task<DefinitionVersionRecord?> GetVersionAsync(string definitionId, int version, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<DefinitionVersionRecord>().AsNoTracking().FirstOrDefaultAsync(v => v.DefinitionId == definitionId && v.Version == version, ct);
    }

    public async Task<IReadOnlyList<DefinitionVersionRecord>> ListVersionsAsync(string definitionId, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<DefinitionVersionRecord>().AsNoTracking().Where(v => v.DefinitionId == definitionId).OrderBy(v => v.Version).ToListAsync(ct);
    }

    public Task AddVersionAsync(DefinitionVersionRecord v, CancellationToken ct = default) { Set<DefinitionVersionRecord>().Add(v); return Task.CompletedTask; }

    public async Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(CancellationToken ct = default) { await FlushAsync(ct); return await Set<TriggerRecord>().AsNoTracking().ToListAsync(ct); }

    public async Task ReplaceTriggersAsync(string definitionId, IReadOnlyList<TriggerRecord> triggers, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        Set<TriggerRecord>().RemoveRange(await Set<TriggerRecord>().Where(t => t.DefinitionId == definitionId).ToListAsync(ct));
        Set<TriggerRecord>().AddRange(triggers);
    }

    public async Task<bool> TryClaimTriggerEventAsync(string definitionId, string eventId, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        if (await Set<TriggerClaimRecord>().AnyAsync(c => c.DefinitionId == definitionId && c.EventId == eventId, ct)) return false;
        Set<TriggerClaimRecord>().Add(new TriggerClaimRecord { DefinitionId = definitionId, EventId = eventId, ClaimedAt = time.GetUtcNow() });
        // Written now: a concurrent claim of the same event fails on the primary key and retries the unit.
        await FlushAsync(ct);
        return true;
    }

    // ── Instances and steps ───────────────────────────────────────────────

    public async Task<InstanceRecord?> GetInstanceAsync(string id, CancellationToken ct = default)
    {
        var local = Set<InstanceRecord>().Local.FirstOrDefault(i => i.Id == id);
        if (local is not null) return local;
        await FlushAsync(ct);
        return await Set<InstanceRecord>().FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    private IQueryable<InstanceRecord> Filter(InstanceQuery q)
    {
        var x = Set<InstanceRecord>().AsQueryable();
        if (q.DefinitionId is not null) x = x.Where(i => i.DefinitionId == q.DefinitionId);
        if (q.DefinitionKey is not null) x = x.Where(i => i.DefinitionKey == q.DefinitionKey);
        if (q.Entity is not null) x = x.Where(i => i.Entity == q.Entity);
        if (q.EntityId is not null) x = x.Where(i => i.EntityId == q.EntityId);
        if (q.ParentId is not null) x = x.Where(i => i.ParentId == q.ParentId);
        if (q.Statuses is not null) { var s = q.Statuses.ToList(); x = x.Where(i => s.Contains(i.Status)); }
        if (q.Kinds is not null) { var k = q.Kinds.ToList(); x = x.Where(i => k.Contains(i.Kind)); }
        return x;
    }

    public async Task<IReadOnlyList<InstanceRecord>> QueryInstancesAsync(InstanceQuery q, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Filter(q).OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Id).Skip(q.Skip).Take(q.Take).ToListAsync(ct);
    }

    public async Task<int> CountInstancesAsync(InstanceQuery q, CancellationToken ct = default) { await FlushAsync(ct); return await Filter(q).CountAsync(ct); }

    public async Task<IReadOnlyDictionary<int, int>> CountInstancesByVersionAsync(string definitionId, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<InstanceRecord>().Where(i => i.DefinitionId == definitionId && (i.Kind == InstanceKinds.Automation || i.Kind == InstanceKinds.Record))
            .GroupBy(i => i.Version).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }
    public Task AddInstanceAsync(InstanceRecord i, CancellationToken ct = default) { Set<InstanceRecord>().Add(i); return Task.CompletedTask; }
    public Task UpdateInstanceAsync(InstanceRecord i, CancellationToken ct = default) { i.RowVersion++; Upsert(i); return Task.CompletedTask; }

    public Task AddStepAsync(StepRecord s, CancellationToken ct = default) { Set<StepRecord>().Add(s); return Task.CompletedTask; }
    public Task UpdateStepAsync(StepRecord s, CancellationToken ct = default) { Upsert(s); return Task.CompletedTask; }

    public async Task<IReadOnlyList<StepRecord>> ListStepsAsync(string instanceId, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<StepRecord>().Where(s => s.InstanceId == instanceId).OrderBy(s => s.StartedAt).ToListAsync(ct);
    }

    // ── Bookmarks ─────────────────────────────────────────────────────────

    public Task AddBookmarkAsync(BookmarkRecord b, CancellationToken ct = default)
    {
        var tracked = Set<BookmarkRecord>().Local.FirstOrDefault(x => x.Id == b.Id);
        if (tracked is not null && db.Entry(tracked).State == EntityState.Deleted) db.Entry(tracked).State = EntityState.Unchanged;
        else Set<BookmarkRecord>().Add(b);
        return Task.CompletedTask;
    }

    public async Task RemoveBookmarkAsync(string id, CancellationToken ct = default)
    {
        var b = Set<BookmarkRecord>().Local.FirstOrDefault(x => x.Id == id) ?? await Set<BookmarkRecord>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is not null) Set<BookmarkRecord>().Remove(b);
    }

    public async Task<BookmarkRecord?> GetBookmarkAsync(string id, CancellationToken ct = default) { await FlushAsync(ct); return await Set<BookmarkRecord>().FirstOrDefaultAsync(b => b.Id == id, ct); }

    public async Task<IReadOnlyList<BookmarkRecord>> ListBookmarksAsync(string instanceId, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<BookmarkRecord>().Where(b => b.InstanceId == instanceId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BookmarkRecord>> ClaimDueBookmarksAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        List<BookmarkRecord> due;
        if (_provider is Provider.Postgres or Provider.SqlServer)
        {
            var et = db.Model.FindEntityType(typeof(BookmarkRecord))!;
            var (table, dueAt, lockedUntil) = (Table(et), Column(et, nameof(BookmarkRecord.DueAt)), Column(et, nameof(BookmarkRecord.LockedUntil)));
            var nowP = ToProvider(et, nameof(BookmarkRecord.DueAt), now);
            var sql = _provider == Provider.Postgres
                ? $"SELECT * FROM {table} WHERE {dueAt} <= {{0}} AND ({lockedUntil} IS NULL OR {lockedUntil} <= {{0}}) ORDER BY {dueAt} LIMIT {limit} FOR UPDATE SKIP LOCKED"
                : $"SELECT TOP ({limit}) * FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK) WHERE {dueAt} <= {{0}} AND ({lockedUntil} IS NULL OR {lockedUntil} <= {{0}}) ORDER BY {dueAt}";
            due = await DbErrors.MapAsync(() => Set<BookmarkRecord>().FromSqlRaw(sql, nowP).ToListAsync(ct));
        }
        else
        {
            due = await Set<BookmarkRecord>().Where(b => b.DueAt != null && b.DueAt <= now && (b.LockedUntil == null || b.LockedUntil <= now))
                .OrderBy(b => b.DueAt).Take(limit).ToListAsync(ct);
        }
        foreach (var b in due) b.LockedUntil = lockUntil;
        return due;
    }

    public async Task<IReadOnlyList<BookmarkRecord>> FindBookmarksAsync(string kind, string? entity, string? entityId, string? signal, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        var x = Set<BookmarkRecord>().Where(b => b.Kind == kind);
        x = entity is null ? x.Where(b => b.Entity == null) : x.Where(b => b.Entity == entity);
        x = entityId is null ? x.Where(b => b.EntityId == null) : x.Where(b => b.EntityId == entityId);
        if (signal is not null) x = x.Where(b => b.Signal == signal);
        return await x.OrderBy(b => b.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BookmarkRecord>> FindDefinitionBookmarksAsync(string definitionId, string kind, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<BookmarkRecord>().Where(b => b.DefinitionId == definitionId && b.Kind == kind && b.InstanceId == null).ToListAsync(ct);
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    public async Task<TaskRecord?> GetTaskAsync(string id, CancellationToken ct = default) { await FlushAsync(ct); return await Set<TaskRecord>().FirstOrDefaultAsync(t => t.Id == id, ct); }

    private IQueryable<TaskRecord> Filter(TaskQuery q)
    {
        var x = Set<TaskRecord>().AsQueryable();
        if (q.InstanceId is not null) x = x.Where(t => t.InstanceId == q.InstanceId);
        if (q.Entity is not null) x = x.Where(t => t.Entity == q.Entity);
        if (q.EntityId is not null) x = x.Where(t => t.EntityId == q.EntityId);
        if (q.Statuses is not null) { var s = q.Statuses.ToList(); x = x.Where(t => s.Contains(t.Status)); }
        // Assignees are a JSON array of ids; match the quoted id inside it.
        if (q.AssigneeId is not null) { var needle = $"\"{q.AssigneeId}\""; x = x.Where(t => t.Assignees.Contains(needle)); }
        if (q.AssigneeAnyOf is { Count: > 0 })
        {
            var needles = q.AssigneeAnyOf.Select(a => $"\"{a}\"").ToList();
            x = x.Where(t => needles.Any(n => t.Assignees.Contains(n)));
        }
        return x;
    }

    public async Task<IReadOnlyList<TaskRecord>> QueryTasksAsync(TaskQuery q, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Filter(q).OrderBy(t => t.Status == Storage.TaskStatus.Open ? 0 : 1).ThenBy(t => t.DueAt).Skip(q.Skip).Take(q.Take).ToListAsync(ct);
    }

    public async Task<int> CountTasksAsync(TaskQuery q, CancellationToken ct = default) { await FlushAsync(ct); return await Filter(q).CountAsync(ct); }
    public Task AddTaskAsync(TaskRecord t, CancellationToken ct = default) { Set<TaskRecord>().Add(t); return Task.CompletedTask; }
    public Task UpdateTaskAsync(TaskRecord t, CancellationToken ct = default) { t.RowVersion++; Upsert(t); return Task.CompletedTask; }

    // ── Outbox ────────────────────────────────────────────────────────────

    public Task AddOutboxAsync(OutboxRecord o, CancellationToken ct = default) { Set<OutboxRecord>().Add(o); return Task.CompletedTask; }
    public Task UpdateOutboxAsync(OutboxRecord o, CancellationToken ct = default) { Upsert(o); return Task.CompletedTask; }

    public async Task<IReadOnlyList<OutboxRecord>> ClaimOutboxAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        List<OutboxRecord> rows;
        if (_provider is Provider.Postgres or Provider.SqlServer)
        {
            var et = db.Model.FindEntityType(typeof(OutboxRecord))!;
            var table = Table(et);
            var (processedAt, lockedUntil, createdAt, id) = (Column(et, nameof(OutboxRecord.ProcessedAt)), Column(et, nameof(OutboxRecord.LockedUntil)),
                Column(et, nameof(OutboxRecord.CreatedAt)), Column(et, nameof(OutboxRecord.Id)));
            var nowP = ToProvider(et, nameof(OutboxRecord.LockedUntil), now);
            var sql = _provider == Provider.Postgres
                ? $"SELECT * FROM {table} WHERE {processedAt} IS NULL AND ({lockedUntil} IS NULL OR {lockedUntil} <= {{0}}) ORDER BY {createdAt}, {id} LIMIT {limit} FOR UPDATE SKIP LOCKED"
                : $"SELECT TOP ({limit}) * FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK) WHERE {processedAt} IS NULL AND ({lockedUntil} IS NULL OR {lockedUntil} <= {{0}}) ORDER BY {createdAt}, {id}";
            rows = await DbErrors.MapAsync(() => Set<OutboxRecord>().FromSqlRaw(sql, nowP).ToListAsync(ct));
        }
        else
        {
            rows = await Set<OutboxRecord>().Where(o => o.ProcessedAt == null && (o.LockedUntil == null || o.LockedUntil <= now))
                .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Take(limit).ToListAsync(ct);
        }
        foreach (var r in rows) r.LockedUntil = lockUntil;
        return rows;
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    public async Task<AuditHead?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default)
    {
        // The engine asks for the head once, right before committing. The unit's own pending rows are
        // written first, so every unit takes its row locks before the head lock (one lock order, fewer
        // deadlocks) and the head stays locked only for the commit itself.
        await FlushAsync(ct);
        var key = tenant ?? "";
        var local = Set<AuditHeadRecord>().Local.FirstOrDefault(h => h.TenantKey == key);
        if (local is not null) return local.Seq == 0 ? null : new AuditHead(local.Seq, local.Hash);
        var et = db.Model.FindEntityType(typeof(AuditHeadRecord))!;
        var (table, tenantKey) = (Table(et), Column(et, nameof(AuditHeadRecord.TenantKey)));
        // Lock the head row for the rest of this transaction: concurrent writers queue here.
        // Table and column names come from the EF model, never from input; the tenant is a parameter.
        var lockSql = _provider switch
        {
            Provider.Postgres => $"SELECT * FROM {table} WHERE {tenantKey} = {{0}} FOR UPDATE",
            Provider.SqlServer => $"SELECT * FROM {table} WITH (UPDLOCK, ROWLOCK) WHERE {tenantKey} = {{0}}",
            _ => null,
        };
        var head = await DbErrors.MapAsync(() => lockSql is null
            ? Set<AuditHeadRecord>().FirstOrDefaultAsync(h => h.TenantKey == key, ct)
            : Set<AuditHeadRecord>().FromSqlRaw(lockSql, key).FirstOrDefaultAsync(ct));
        if (head is null)
        {
            // The first entry ever: a concurrent first writer fails on the primary key at commit and retries.
            head = new AuditHeadRecord { TenantKey = key };
            Set<AuditHeadRecord>().Add(head);
        }
        // The head row is the chain's own state; it never depends on the newest entry still existing.
        return head.Seq == 0 ? null : new AuditHead(head.Seq, head.Hash);
    }

    public async Task SetAuditHeadAsync(string? tenant, AuditHead head, CancellationToken ct = default)
    {
        var key = tenant ?? "";
        var row = Set<AuditHeadRecord>().Local.FirstOrDefault(h => h.TenantKey == key) ?? await Set<AuditHeadRecord>().FirstOrDefaultAsync(h => h.TenantKey == key, ct);
        if (row is null) Set<AuditHeadRecord>().Add(new AuditHeadRecord { TenantKey = key, Seq = head.Seq, Hash = head.Hash });
        else
        {
            row.Seq = head.Seq;
            row.Hash = head.Hash;
        }
    }

    public Task AddAuditAsync(AuditRecord a, CancellationToken ct = default)
    {
        Set<AuditRecord>().Add(a);
        db.Entry(a).Property(WorkflowModelBuilderExtensions.AuditTenantKey).CurrentValue = a.Tenant ?? "";
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        var x = Set<AuditRecord>().AsNoTracking().AsQueryable();
        if (q.Category is not null) x = x.Where(a => a.Category == q.Category);
        if (q.Action is not null) x = x.Where(a => a.Action == q.Action);
        if (q.Entity is not null) x = x.Where(a => a.Entity == q.Entity);
        if (q.EntityId is not null) x = x.Where(a => a.EntityId == q.EntityId);
        if (q.InstanceId is not null) x = x.Where(a => a.InstanceId == q.InstanceId || a.CorrelationId == q.InstanceId);
        if (q.DefinitionId is not null) x = x.Where(a => a.DefinitionId == q.DefinitionId);
        if (q.ActorId is not null) x = x.Where(a => a.ActorId == q.ActorId);
        if (q.From is not null) x = x.Where(a => a.At >= q.From);
        if (q.To is not null) x = x.Where(a => a.At < q.To);
        if (q.Before is not null) x = x.Where(a => a.Seq < q.Before);
        if (q.FromSeq is not null) x = x.Where(a => a.Seq >= q.FromSeq);
        x = q.FromSeq is not null ? x.OrderBy(a => a.Seq) : x.OrderByDescending(a => a.Seq);
        return await x.Take(q.Take).ToListAsync(ct);
    }

    public async Task<AuditPurgeResult> PurgeAuditAsync(string category, DateTimeOffset before, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        // Page through the victims' sequence numbers so the checkpoint records exactly what went, however many.
        const int page = 10_000;
        var seqs = new List<long>();
        long after = 0;
        while (true)
        {
            var batch = await Set<AuditRecord>().AsNoTracking().Where(a => a.Category == category && a.At < before && a.Seq > after)
                .OrderBy(a => a.Seq).Select(a => a.Seq).Take(page).ToListAsync(ct);
            seqs.AddRange(batch);
            if (batch.Count < page) break;
            after = batch[^1];
        }
        var count = 0;
        var ranges = SeqRange.Compress(seqs);
        foreach (var r in ranges)
            count += await Set<AuditRecord>().Where(a => a.Category == category && a.Seq >= r.From && a.Seq <= r.To).ExecuteDeleteAsync(ct);
        return new AuditPurgeResult(count, ranges);
    }

    public async Task<int> CleanupAsync(DateTimeOffset processedOutboxBefore, DateTimeOffset claimsBefore, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        var n = await Set<OutboxRecord>().Where(o => o.ProcessedAt != null && o.ProcessedAt < processedOutboxBefore).ExecuteDeleteAsync(ct);
        n += await Set<TriggerClaimRecord>().Where(c => c.ClaimedAt < claimsBefore).ExecuteDeleteAsync(ct);
        return n;
    }

    // ── Settings ──────────────────────────────────────────────────────────

    public async Task<SettingsRecord?> GetSettingsAsync(CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<SettingsRecord>().FirstOrDefaultAsync(s => s.Key == "global", ct);
    }

    public async Task SaveSettingsAsync(SettingsRecord s, CancellationToken ct = default)
    {
        var existing = await GetSettingsAsync(ct);
        if (existing is null)
        {
            s.Key = "global";
            Set<SettingsRecord>().Add(s);
            return;
        }
        existing.Json = s.Json;
        existing.Revision = s.Revision;
        existing.UpdatedAt = s.UpdatedAt;
        existing.UpdatedBy = s.UpdatedBy;
    }

    // ── Unit of work ──────────────────────────────────────────────────────

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await DbErrors.MapAsync(async () =>
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _committed = true;
    }

    // ── Savepoints ────────────────────────────────────────────────────────

    public async Task<bool> CreateSavepointAsync(string name, CancellationToken ct = default)
    {
        if (!tx.SupportsSavepoints) return false;
        await FlushAsync(ct);
        await DbErrors.MapAsync(() => tx.CreateSavepointAsync(name, ct));
        // What the tracker holds now; anything tracked later belongs to the work a rollback undoes.
        _savepoints[name] = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        return true;
    }

    public async Task RollbackToSavepointAsync(string name, CancellationToken ct = default)
    {
        if (!_savepoints.Remove(name, out var before)) return;
        await tx.RollbackToSavepointAsync(name, ct);
        // The database is back at the savepoint; bring the app's tracked records back with it. Workflow
        // rows are left alone: the engine saved all of its rows before the savepoint and did not touch them since.
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (IsWorkflowRow(entry.Metadata.ClrType)) continue;
            if (!before.Contains(entry.Entity)) entry.State = EntityState.Detached;
            else await entry.ReloadAsync(ct);
        }
    }

    public async Task ReleaseSavepointAsync(string name, CancellationToken ct = default)
    {
        if (!_savepoints.Remove(name)) return;
        await tx.ReleaseSavepointAsync(name, ct);
    }

    private static bool IsWorkflowRow(Type t) => t.Namespace == typeof(InstanceRecord).Namespace || t == typeof(AuditHeadRecord) || t == typeof(TriggerClaimRecord);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_committed) await tx.RollbackAsync();
        }
        catch (Exception)
        {
            // Rolling back after a failed commit can fail too; the caller already has the real error.
        }
        finally
        {
            await tx.DisposeAsync();
            await scope.DisposeAsync();
        }
    }

    private static string Quote(string name) => $"\"{name}\"";

    private static string Table(IEntityType et)
    {
        var schema = et.GetSchema();
        var name = et.GetTableName()!;
        return schema is null ? Quote(name) : $"{Quote(schema)}.{Quote(name)}";
    }

    /// <summary>A property's column name as mapped (hosts may rename columns), quoted.</summary>
    private static string Column(IEntityType et, string property) =>
        Quote(et.FindProperty(property)!.GetColumnName(StoreObjectIdentifier.Table(et.GetTableName()!, et.GetSchema()))!);

    /// <summary>A value as the property's column stores it (instants may be ticks on SQLite).</summary>
    private static object ToProvider(IEntityType et, string property, object value) =>
        et.FindProperty(property)!.GetTypeMapping().Converter is { } conv ? conv.ConvertToProvider(value)! : value;
}

/// <summary>Reads and writes the app's own entities through the registry's CLR bindings.</summary>
internal sealed class EfEntityGateway(DbContext db, WorkflowRegistry registry) : IEntityGateway
{
    private EntityDescriptor Entity(string key) =>
        registry.Entity(key) is { ClrType: not null } e ? e : throw new WorkflowException("unknownEntity", $"'{key}' is not bound to an EF Core entity.");

    private async Task<object?> FindAsync(EntityDescriptor e, string id, CancellationToken ct)
    {
        var et = db.Model.FindEntityType(e.ClrType!) ?? throw new WorkflowException("unknownEntity", $"{e.ClrType!.Name} is not part of the DbContext.");
        var keyType = et.FindPrimaryKey()!.Properties[0].ClrType;
        object key;
        try
        {
            key = keyType == typeof(Guid) ? Guid.Parse(id) : keyType == typeof(string) ? id : Convert.ChangeType(id, keyType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception x) when (x is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
        return await db.FindAsync(e.ClrType!, [key], ct);
    }

    internal async Task<Dictionary<string, object?>> SnapshotAsync(EntityDescriptor e, object entity, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var entry = db.Entry(entity);
        foreach (var f in e.Fields)
        {
            if (f.Type == FieldTypes.Collection) continue;
            // Load the navigation a nested or relation field reads from, when EF has not loaded it yet.
            var first = f.MemberPath?.Split('.')[0];
            if (first is not null && (f.Type == FieldTypes.Relation || f.MemberPath!.Contains('.')))
            {
                var nav = entry.Navigations.FirstOrDefault(n => n.Metadata.Name == first);
                if (nav is { IsLoaded: false }) await nav.LoadAsync(ct);
            }
            var v = f.Getter?.Invoke(entity);
            if (f.Type == FieldTypes.Relation)
            {
                // Record ids are strings everywhere in workflows, whatever the key type.
                v = v is null || f.Entity is null ? null : registry.Entity(f.Entity)?.IdGetter?.Invoke(v) is { } rid ? Values.Text(rid) : null;
            }
            values[f.Key] = Values.Normalize(v);
        }
        return values;
    }

    public async Task<RecordSnapshot?> GetAsync(string entity, string id, CancellationToken ct = default)
    {
        var e = Entity(entity);
        var obj = await FindAsync(e, id, ct);
        if (obj is null) return null;
        return new RecordSnapshot { Entity = entity, Id = id, Values = await SnapshotAsync(e, obj, ct) };
    }

    public async Task<IReadOnlyList<RecordSnapshot>> GetCollectionAsync(string entity, string id, string field, CancellationToken ct = default)
    {
        var e = Entity(entity);
        var f = e.Field(field);
        var obj = await FindAsync(e, id, ct);
        if (obj is null || f?.Type != FieldTypes.Collection || f.Entity is null) return [];
        var nav = db.Entry(obj).Collections.FirstOrDefault(c => c.Metadata.Name == f.MemberPath);
        if (nav is { IsLoaded: false }) await nav.LoadAsync(ct);
        var child = Entity(f.Entity);
        var list = new List<RecordSnapshot>();
        foreach (var item in (f.Getter?.Invoke(obj) as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object>())
        {
            var cid = Values.Text(child.IdGetter!(item));
            list.Add(new RecordSnapshot { Entity = f.Entity, Id = cid, Values = await SnapshotAsync(child, item, ct) });
        }
        return list;
    }

    public async Task SetFieldsAsync(string entity, string id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        var e = Entity(entity);
        var obj = await FindAsync(e, id, ct) ?? throw new WorkflowException("noRecord", $"{e.Label} {id} does not exist.");
        foreach (var (k, v) in values)
        {
            var f = e.Field(k) ?? throw new WorkflowException("unknownField", $"'{k}' is not a field of {e.Label}.");
            if (f.Setter is null) throw new WorkflowException("fieldNotSettable", $"{f.Label} cannot be changed by workflows.");
            f.Setter(obj, v);
        }
        // Saved now (inside the unit's transaction) so the change hook runs while the workflow's write scope is active.
        await DbErrors.MapAsync(() => db.SaveChangesAsync(ct));
    }

    public Task<IReadOnlyList<string>> ListIdsAsync(string entity, string? after, int limit, CancellationToken ct = default)
    {
        var e = Entity(entity);
        var et = db.Model.FindEntityType(e.ClrType!)!;
        var key = et.FindPrimaryKey()!.Properties[0];
        var method = typeof(EfEntityGateway).GetMethod(nameof(PageIdsAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(e.ClrType!, key.ClrType);
        return (Task<IReadOnlyList<string>>)method.Invoke(this, [key.Name, after, limit, ct])!;
    }

    private async Task<IReadOnlyList<string>> PageIdsAsync<TEntity, TKey>(string keyName, string? after, int limit, CancellationToken ct) where TEntity : class
    {
        IQueryable<TEntity> q = db.Set<TEntity>().AsNoTracking();
        if (after is not null)
        {
            var cursor = ConvertKey<TKey>(after);
            var p = Expression.Parameter(typeof(TEntity), "x");
            Expression prop = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(TKey)], p, Expression.Constant(keyName));
            Expression value = Expression.Constant(cursor, typeof(TKey));
            // Keys without a ">" operator (string, Guid) compare through Compare/CompareTo, which EF translates.
            Expression body = typeof(TKey) == typeof(string)
                ? Expression.GreaterThan(Expression.Call(typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!, prop, value), Expression.Constant(0))
                : typeof(TKey) == typeof(Guid)
                    ? Expression.GreaterThan(Expression.Call(prop, typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!, value), Expression.Constant(0))
                    : Expression.GreaterThan(prop, value);
            q = q.Where(Expression.Lambda<Func<TEntity, bool>>(body, p));
        }
        var ids = await q.OrderBy(x => EF.Property<TKey>(x, keyName)).Select(x => EF.Property<TKey>(x, keyName)).Take(limit).ToListAsync(ct);
        return ids.Select(i => Values.Text(i)).ToList();
    }

    private static TKey ConvertKey<TKey>(string id) =>
        typeof(TKey) == typeof(Guid) ? (TKey)(object)Guid.Parse(id) : typeof(TKey) == typeof(string) ? (TKey)(object)id : (TKey)Convert.ChangeType(id, typeof(TKey), System.Globalization.CultureInfo.InvariantCulture);
}
