using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class EfWorkflowStore<TContext>(IServiceScopeFactory scopes, WorkflowRegistry registry) : IWorkflowStore where TContext : DbContext
{
    public async Task<IWorkflowSession> BeginAsync(CancellationToken ct = default)
    {
        var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var tx = await db.Database.BeginTransactionAsync(ct);
        return new EfWorkflowSession(db, tx, scope, registry);
    }
}

internal sealed class EfWorkflowSession(DbContext db, IDbContextTransaction tx, AsyncServiceScope scope, WorkflowRegistry registry) : IWorkflowSession
{
    private bool _committed;
    private readonly Provider _provider = ProviderOf(db);

    public IEntityGateway Entities { get; } = new EfEntityGateway(db, registry);
    public IServiceProvider Services => scope.ServiceProvider;
    internal DbContext Db => db;

    private enum Provider { Postgres, SqlServer, Sqlite, Other }

    private static Provider ProviderOf(DbContext db) => db.Database.ProviderName switch
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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw new WorkflowConcurrencyException(e.Message);
        }
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
        Set<TriggerClaimRecord>().Add(new TriggerClaimRecord { DefinitionId = definitionId, EventId = eventId, ClaimedAt = DateTimeOffset.UtcNow });
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
        var et = db.Model.FindEntityType(typeof(BookmarkRecord))!;
        var table = Table(et);
        if (_provider is Provider.Postgres or Provider.SqlServer)
        {
            var nowP = Param(now);
            var sql = _provider == Provider.Postgres
                ? $"SELECT * FROM {table} WHERE due_at <= {{0}} AND (locked_until IS NULL OR locked_until <= {{0}}) ORDER BY due_at LIMIT {limit} FOR UPDATE SKIP LOCKED"
                : $"SELECT TOP ({limit}) * FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK) WHERE due_at <= {{0}} AND (locked_until IS NULL OR locked_until <= {{0}}) ORDER BY due_at";
            due = await Set<BookmarkRecord>().FromSqlRaw(sql, nowP).ToListAsync(ct);
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
        if (entity is not null) x = x.Where(b => b.Entity == entity);
        if (entityId is not null) x = x.Where(b => b.EntityId == entityId);
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
        var table = Table(db.Model.FindEntityType(typeof(OutboxRecord))!);
        if (_provider is Provider.Postgres or Provider.SqlServer)
        {
            var sql = _provider == Provider.Postgres
                ? $"SELECT * FROM {table} WHERE processed_at IS NULL AND (locked_until IS NULL OR locked_until <= {{0}}) ORDER BY created_at, id LIMIT {limit} FOR UPDATE SKIP LOCKED"
                : $"SELECT TOP ({limit}) * FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK) WHERE processed_at IS NULL AND (locked_until IS NULL OR locked_until <= {{0}}) ORDER BY created_at, id";
            rows = await Set<OutboxRecord>().FromSqlRaw(sql, Param(now)).ToListAsync(ct);
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

    public async Task<AuditRecord?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        var key = tenant ?? "";
        var table = Table(db.Model.FindEntityType(typeof(AuditHeadRecord))!);
        // Lock the head row for the rest of this transaction: concurrent writers queue here.
        // The table name comes from the EF model, never from input; the tenant is a parameter.
        var lockSql = _provider switch
        {
            Provider.Postgres => "SELECT * FROM " + table + " WHERE tenant_key = {0} FOR UPDATE",
            Provider.SqlServer => "SELECT * FROM " + table + " WITH (UPDLOCK, ROWLOCK) WHERE tenant_key = {0}",
            _ => null,
        };
        var head = lockSql is null
            ? await Set<AuditHeadRecord>().FirstOrDefaultAsync(h => h.TenantKey == key, ct)
            : await Set<AuditHeadRecord>().FromSqlRaw(lockSql, key).FirstOrDefaultAsync(ct);
        if (head is null)
        {
            head = new AuditHeadRecord { TenantKey = key };
            Set<AuditHeadRecord>().Add(head);
            await FlushAsync(ct);
        }
        if (head.Seq == 0) return null;
        return await Set<AuditRecord>().AsNoTracking().FirstOrDefaultAsync(a => a.Tenant == tenant && a.Seq == head.Seq, ct);
    }

    public async Task AddAuditAsync(AuditRecord a, CancellationToken ct = default)
    {
        Set<AuditRecord>().Add(a);
        var key = a.Tenant ?? "";
        var head = Set<AuditHeadRecord>().Local.FirstOrDefault(h => h.TenantKey == key) ?? await Set<AuditHeadRecord>().FirstOrDefaultAsync(h => h.TenantKey == key, ct);
        if (head is null) Set<AuditHeadRecord>().Add(new AuditHeadRecord { TenantKey = key, Seq = a.Seq, Hash = a.Hash });
        else
        {
            head.Seq = a.Seq;
            head.Hash = a.Hash;
        }
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

    public async Task<int> PurgeAuditAsync(string category, DateTimeOffset before, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        return await Set<AuditRecord>().Where(a => a.Category == category && a.At < before).ExecuteDeleteAsync(ct);
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
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _committed = true;
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw new WorkflowConcurrencyException(e.Message);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            throw new WorkflowConcurrencyException(e.InnerException?.Message ?? e.Message);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException e)
    {
        var m = e.InnerException?.Message ?? "";
        return m.Contains("23505", StringComparison.Ordinal) || m.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               || m.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase) || m.Contains("PRIMARY KEY", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed) await tx.RollbackAsync();
        await tx.DisposeAsync();
        await scope.DisposeAsync();
    }

    private static string Table(Microsoft.EntityFrameworkCore.Metadata.IEntityType et)
    {
        var schema = et.GetSchema();
        var name = et.GetTableName();
        return schema is null ? $"\"{name}\"" : $"\"{schema}\".\"{name}\"";
    }

    private object Param(DateTimeOffset now) =>
        db.Model.FindEntityType(typeof(BookmarkRecord))!.FindProperty(nameof(BookmarkRecord.DueAt))!.GetValueConverter() is { } conv
            ? conv.ConvertToProvider(now)!
            : now;
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
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListIdsAsync(string entity, int limit, CancellationToken ct = default)
    {
        var e = Entity(entity);
        var et = db.Model.FindEntityType(e.ClrType!)!;
        var keyName = et.FindPrimaryKey()!.Properties[0].Name;
        var set = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(e.ClrType!).Invoke(db, null)!;
        var ids = await set.OrderByDescending(x => EF.Property<object>(x, keyName)).Take(limit).Select(x => EF.Property<object>(x, keyName)).ToListAsync(ct);
        return ids.Select(i => Values.Text(i)).ToList();
    }
}
