using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore;

/// <summary>
/// Watches SaveChanges for registered entities. Before the write it enforces lifecycle field
/// ownership and fills in initial lifecycle states; it then adds change events to the outbox in the
/// same transaction, so workflows start only for changes that really committed (spec §5).
/// </summary>
/// <remarks>
/// <para>
/// Writes that bypass the change tracker (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL) are not
/// seen. Report deliberate bulk changes with <c>WorkflowEngine.Changes</c> or avoid them for watched fields.
/// </para>
/// <para>
/// Disconnected updates (<c>db.Update(entity)</c>) are seen: when a registered field is marked modified
/// but EF has no original value for it, the value stored in the database is read and used instead.
/// </para>
/// <para>
/// New records get their ids from the database, so their <c>created</c> events are written by a second
/// save right after the insert. Without a transaction of the caller's, the interceptor opens one so the
/// record and its event still commit together. If that second save fails, the transaction is rolled
/// back and the context's change tracker is cleared (its entries would otherwise claim rows that do not
/// exist); discard the context. With a retrying execution strategy (<c>EnableRetryOnFailure</c>) EF does not
/// allow the interceptor's own transaction: create watched records inside
/// <c>db.Database.CreateExecutionStrategy().ExecuteAsync(...)</c> with your own transaction.
/// </para>
/// </remarks>
public sealed class WorkflowSaveChangesInterceptor(WorkflowEngine engine) : SaveChangesInterceptor
{
    private sealed class Pending
    {
        public List<(EntityEntry Entry, EntityDescriptor Entity, Dictionary<string, object?> After)> Added { get; } = [];
        /// <summary>Outbox rows this save added, removed again if the save fails (so a retried save does not duplicate them).</summary>
        public List<OutboxRecord> Outbox { get; } = [];
        public IDbContextTransaction? OwnTransaction { get; set; }
        public bool Reentrant { get; set; }
    }

    private readonly ConditionalWeakTable<DbContext, Pending> _pending = [];
    private readonly Dictionary<Type, EntityDescriptor> _byClrType = engine.Registry.Entities.Where(e => e.ClrType is not null).ToDictionary(e => e.ClrType!);
    private readonly ConcurrentDictionary<IEntityType, EntityDescriptor?> _byEntityType = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db) Before(db, sync: true).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db) await Before(db, sync: false, ct);
        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } db) After(db, sync: true).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db) await After(db, sync: false, ct);
        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Fail(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        Fail(eventData.Context);
        return Task.CompletedTask;
    }

    private void Fail(DbContext? db)
    {
        if (db is null || !_pending.TryGetValue(db, out var p)) return;
        // The nested save of created events failing is handled in After; here only the caller's save failed.
        if (p.Reentrant) return;
        foreach (var o in p.Outbox)
        {
            var entry = db.Entry(o);
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
        }
        p.Outbox.Clear();
        p.Added.Clear();
        if (p.OwnTransaction is { } tx)
        {
            try
            {
                tx.Rollback();
            }
            finally
            {
                tx.Dispose();
                p.OwnTransaction = null;
            }
        }
        _pending.Remove(db);
    }

    /// <summary>The registry entity an EF entry belongs to: its CLR type or a base type (TPH subclasses, lazy-loading proxies).</summary>
    private EntityDescriptor? Describe(EntityEntry entry) => _byEntityType.GetOrAdd(entry.Metadata, et =>
    {
        for (IReadOnlyEntityType? t = et; t is not null; t = t.BaseType)
            if (_byClrType.TryGetValue(t.ClrType, out var d)) return d;
        for (var t = et.ClrType; t is not null && t != typeof(object); t = t.BaseType)
            if (_byClrType.TryGetValue(t, out var d)) return d;
        return null;
    });

    private async Task Before(DbContext db, bool sync, CancellationToken ct = default)
    {
        var p = _pending.GetOrCreateValue(db);
        if (p.Reentrant) return;
        try
        {
            await InspectAsync(db, p, sync, ct);
        }
        catch
        {
            // A refused write (or any failure here) means nothing is saved; forget what this save prepared.
            Fail(db);
            throw;
        }
    }

    private async Task InspectAsync(DbContext db, Pending p, bool sync, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        var outbox = new List<OutboxRecord>();
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (Describe(entry) is not { } desc) continue;
            switch (entry.State)
            {
                case EntityState.Added:
                {
                    var after = ReadFields(entry, desc, original: false);
                    // Inspect now with a placeholder id: this applies initial lifecycle states before the insert.
                    var r = engine.Changes.Inspect(desc.Key, "(new)", ChangeKinds.Created, null, after);
                    foreach (var (k, v) in r.InitialValues)
                    {
                        desc.Field(k)?.Setter?.Invoke(entry.Entity, v);
                        after[k] = v;
                    }
                    p.Added.Add((entry, desc, after));
                    break;
                }
                case EntityState.Modified:
                {
                    var id = Values.Text(desc.IdGetter!(entry.Entity));
                    var before = await OriginalFieldsAsync(entry, desc, sync, ct);
                    var r = engine.Changes.Inspect(desc.Key, id, ChangeKinds.Updated, before, ReadFields(entry, desc, original: false));
                    if (r.Change is not null) outbox.Add(engine.Changes.ToOutbox(r.Change));
                    break;
                }
                case EntityState.Deleted:
                {
                    var id = Values.Text(desc.IdGetter!(entry.Entity));
                    var r = engine.Changes.Inspect(desc.Key, id, ChangeKinds.Deleted, ReadFields(entry, desc, original: true), null);
                    if (r.Change is not null) outbox.Add(engine.Changes.ToOutbox(r.Change));
                    break;
                }
            }
        }
        if (outbox.Count > 0)
        {
            db.Set<OutboxRecord>().AddRange(outbox);
            p.Outbox.AddRange(outbox);
        }
        if (p.Added.Count > 0 && db.Database.CurrentTransaction is null && p.OwnTransaction is null)
        {
            if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
            {
                throw new InvalidOperationException(
                    "Workflows record new records and their 'created' event in one transaction, which a retrying execution strategy does not allow the " +
                    "interceptor to open. Save new records inside db.Database.CreateExecutionStrategy().ExecuteAsync(...) with your own transaction.");
            }
            p.OwnTransaction = sync ? db.Database.BeginTransaction() : await db.Database.BeginTransactionAsync(ct);
        }
    }

    private async Task After(DbContext db, bool sync, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(db, out var p) || p.Reentrant) return;
        p.Outbox.Clear();
        try
        {
            if (p.Added.Count > 0)
            {
                foreach (var (entry, desc, after) in p.Added)
                {
                    var id = Values.Text(desc.IdGetter!(entry.Entity));
                    var scope = WorkflowWriteScope.Current;
                    var actor = scope?.Actor ?? engine.CurrentActor();
                    // Ownership was already checked; this only builds the event with the real id.
                    var change = new EntityChange
                    {
                        Kind = ChangeKinds.Created, Entity = desc.Key, EntityId = id,
                        Fields = after.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => new FieldChange { To = kv.Value }),
                        ActorType = actor?.Type ?? "system", ActorId = actor?.Id, ActorName = actor?.Name,
                        Depth = scope?.Depth ?? 0, CausationInstanceId = scope?.InstanceId, CausationDefinitionId = scope?.DefinitionId,
                        CorrelationId = scope?.CorrelationId, At = engine.Options.Time.GetUtcNow(),
                    };
                    db.Set<OutboxRecord>().Add(engine.Changes.ToOutbox(change));
                }
                p.Added.Clear();
                p.Reentrant = true;
                try
                {
                    if (sync) db.SaveChanges();
                    else await db.SaveChangesAsync(ct);
                }
                catch
                {
                    AbandonAfterFailedEventSave(db, p);
                    throw;
                }
                p.Reentrant = false;
            }
            if (p.OwnTransaction is { } tx)
            {
                if (sync) tx.Commit();
                else await tx.CommitAsync(ct);
                await tx.DisposeAsync();
                p.OwnTransaction = null;
            }
        }
        finally
        {
            p.Reentrant = false;
            if (p.OwnTransaction is null) _pending.Remove(db);
        }
    }

    /// <summary>
    /// The created events could not be written after the records were: roll back what this save did. EF
    /// already accepted the changes, so its tracker would claim rows that no longer exist; it is cleared.
    /// </summary>
    private void AbandonAfterFailedEventSave(DbContext db, Pending p)
    {
        if (p.OwnTransaction is { } tx)
        {
            try
            {
                tx.Rollback();
            }
            catch (Exception)
            {
                // The original failure is what the caller needs to see.
            }
            tx.Dispose();
            p.OwnTransaction = null;
            db.ChangeTracker.Clear();
        }
        _pending.Remove(db);
    }

    /// <summary>
    /// The registered field values before the write. A field marked modified whose original value equals
    /// its current one comes from a disconnected update (<c>db.Update</c>): EF never saw the old value, so
    /// the database's value is used.
    /// </summary>
    private static async Task<Dictionary<string, object?>> OriginalFieldsAsync(EntityEntry entry, EntityDescriptor desc, bool sync, CancellationToken ct)
    {
        var before = ReadFields(entry, desc, original: true);
        var unknown = new List<(string Field, PropertyEntry Property)>();
        foreach (var f in desc.Fields)
        {
            if (FieldProperty(entry, f) is not { IsModified: true } prop) continue;
            if (Equals(prop.OriginalValue, prop.CurrentValue)) unknown.Add((f.Key, prop));
        }
        if (unknown.Count == 0) return before;
        var stored = sync ? entry.GetDatabaseValues() : await entry.GetDatabaseValuesAsync(ct);
        if (stored is null) return before;
        foreach (var (field, prop) in unknown) before[field] = Values.Normalize(stored[prop.Metadata]);
        return before;
    }

    /// <summary>The tracked property behind a registered top-level field (relations: their foreign key).</summary>
    private static PropertyEntry? FieldProperty(EntityEntry entry, FieldDescriptor f)
    {
        if (f.MemberPath is null || f.MemberPath.Contains('.') || f.Type == FieldTypes.Collection) return null;
        if (f.Type == FieldTypes.Relation)
        {
            var nav = entry.References.FirstOrDefault(r => r.Metadata.Name == f.MemberPath);
            var fk = nav?.Metadata is INavigation n && n.IsOnDependent ? n.ForeignKey.Properties[0] : null;
            return fk is null ? null : entry.Property(fk.Name);
        }
        return entry.Properties.FirstOrDefault(x => x.Metadata.Name == f.MemberPath);
    }

    /// <summary>Registered top-level field values from the tracker (original or current). Relations use their foreign key.</summary>
    private static Dictionary<string, object?> ReadFields(EntityEntry entry, EntityDescriptor desc, bool original)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in desc.Fields)
        {
            if (f.MemberPath is null || f.MemberPath.Contains('.') || f.Type == FieldTypes.Collection) continue;
            var p = FieldProperty(entry, f);
            if (p is null && f.Type == FieldTypes.Relation) continue;
            values[f.Key] = p is null ? Values.Normalize(f.Getter?.Invoke(entry.Entity)) : Values.Normalize(original ? p.OriginalValue : p.CurrentValue);
        }
        return values;
    }
}
