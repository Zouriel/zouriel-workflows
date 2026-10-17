using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// Writes that bypass the change tracker (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL) are not
/// seen. Report deliberate bulk changes with <c>WorkflowEngine.Changes</c> or avoid them for watched fields.
/// </remarks>
public sealed class WorkflowSaveChangesInterceptor(WorkflowEngine engine) : SaveChangesInterceptor
{
    private sealed class Pending
    {
        public List<(EntityEntry Entry, EntityDescriptor Entity, Dictionary<string, object?> After)> Added { get; } = [];
        public IDbContextTransaction? OwnTransaction { get; set; }
        public bool Reentrant { get; set; }
    }

    private readonly ConditionalWeakTable<DbContext, Pending> _pending = [];
    private readonly Dictionary<Type, EntityDescriptor> _byType = engine.Registry.Entities.Where(e => e.ClrType is not null).ToDictionary(e => e.ClrType!);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db) Before(db, sync: true).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db) await Before(db, sync: false, ct);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } db) After(db, sync: true).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db) await After(db, sync: false, ct);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Fail(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        Fail(eventData.Context);
        return Task.CompletedTask;
    }

    private void Fail(DbContext? db)
    {
        if (db is null || !_pending.TryGetValue(db, out var p)) return;
        p.OwnTransaction?.Rollback();
        p.OwnTransaction?.Dispose();
        _pending.Remove(db);
    }

    private async Task Before(DbContext db, bool sync, CancellationToken ct = default)
    {
        var p = _pending.GetOrCreateValue(db);
        if (p.Reentrant) return;
        db.ChangeTracker.DetectChanges();
        var outbox = new List<OutboxRecord>();
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (!_byType.TryGetValue(entry.Entity.GetType(), out var desc)) continue;
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
                    var r = engine.Changes.Inspect(desc.Key, id, ChangeKinds.Updated, ReadFields(entry, desc, original: true), ReadFields(entry, desc, original: false));
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
        if (outbox.Count > 0) db.Set<OutboxRecord>().AddRange(outbox);
        // New records get their ids from the database, so their events are written after the insert.
        // Without a transaction of the caller's, open one so record and event still commit together.
        if (p.Added.Count > 0 && db.Database.CurrentTransaction is null && p.OwnTransaction is null)
            p.OwnTransaction = sync ? db.Database.BeginTransaction() : await db.Database.BeginTransactionAsync(ct);
    }

    private async Task After(DbContext db, bool sync, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(db, out var p) || p.Reentrant) return;
        try
        {
            if (p.Added.Count > 0)
            {
                foreach (var (entry, desc, after) in p.Added)
                {
                    var id = Values.Text(desc.IdGetter!(entry.Entity));
                    // Ownership was already checked; this only builds the event with the real id.
                    var change = new EntityChange
                    {
                        Kind = ChangeKinds.Created, Entity = desc.Key, EntityId = id,
                        Fields = after.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => new FieldChange { To = kv.Value }),
                        ActorType = WorkflowWriteScope.Current?.Actor.Type ?? engine.CurrentActor()?.Type ?? "system",
                        ActorId = WorkflowWriteScope.Current?.Actor.Id ?? engine.CurrentActor()?.Id,
                        ActorName = WorkflowWriteScope.Current?.Actor.Name ?? engine.CurrentActor()?.Name,
                        Depth = WorkflowWriteScope.Current?.Depth ?? 0, CausationInstanceId = WorkflowWriteScope.Current?.InstanceId,
                        CausationDefinitionId = WorkflowWriteScope.Current?.DefinitionId, CorrelationId = WorkflowWriteScope.Current?.CorrelationId,
                        At = engine.Options.Time.GetUtcNow(),
                    };
                    db.Set<OutboxRecord>().Add(engine.Changes.ToOutbox(change));
                }
                p.Added.Clear();
                p.Reentrant = true;
                if (sync) db.SaveChanges();
                else await db.SaveChangesAsync(ct);
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

    /// <summary>Registered top-level field values from the tracker (original or current). Relations use their foreign key.</summary>
    private static Dictionary<string, object?> ReadFields(EntityEntry entry, EntityDescriptor desc, bool original)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in desc.Fields)
        {
            if (f.MemberPath is null || f.MemberPath.Contains('.') || f.Type == FieldTypes.Collection) continue;
            if (f.Type == FieldTypes.Relation)
            {
                var nav = entry.References.FirstOrDefault(r => r.Metadata.Name == f.MemberPath);
                var fk = nav?.Metadata is Microsoft.EntityFrameworkCore.Metadata.INavigation n && n.IsOnDependent ? n.ForeignKey.Properties[0] : null;
                if (fk is null) continue;
                var prop = entry.Property(fk.Name);
                values[f.Key] = Values.Normalize(original ? prop.OriginalValue : prop.CurrentValue);
                continue;
            }
            var p = entry.Properties.FirstOrDefault(x => x.Metadata.Name == f.MemberPath);
            values[f.Key] = p is null ? Values.Normalize(f.Getter?.Invoke(entry.Entity)) : Values.Normalize(original ? p.OriginalValue : p.CurrentValue);
        }
        return values;
    }
}
