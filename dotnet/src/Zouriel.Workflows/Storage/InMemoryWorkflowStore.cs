using System.Text.Json;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Storage;

/// <summary>
/// A complete store kept in memory, with real transactions: a session sees its own writes, and
/// nothing reaches the shared data until <c>CommitAsync</c>. Used by conformance tests, test runs
/// (<c>/simulate</c>) and demos. It also holds records, so it doubles as the entity gateway.
/// </summary>
public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    internal readonly object Gate = new();
    internal readonly Dictionary<string, Dictionary<string, object>> Tables = [];
    internal long RecordOrder;

    /// <summary>Set after the engine is created, so record writes can be inspected (change events, owned fields).</summary>
    public ChangeInspector? Changes { get; set; }

    /// <summary>Reads records that are not in memory from here (test runs read the real app's records).</summary>
    public IEntityGateway? Fallback { get; set; }

    public Task<IWorkflowSession> BeginAsync(CancellationToken ct = default) => Task.FromResult<IWorkflowSession>(new Session(this));

    // ── Host-side record changes (what the app does outside workflows) ────

    public async Task CreateRecordAsync(string entity, string id, Dictionary<string, object?> values, CancellationToken ct = default)
    {
        await using var s = (Session)await BeginAsync(ct);
        await s.Records.CreateAsync(entity, id, values, ct);
        await s.CommitAsync(ct);
    }

    public async Task ChangeRecordAsync(string entity, string id, Dictionary<string, object?> values, CancellationToken ct = default)
    {
        await using var s = (Session)await BeginAsync(ct);
        await s.Records.SetFieldsAsync(entity, id, values, ct);
        await s.CommitAsync(ct);
    }

    public async Task DeleteRecordAsync(string entity, string id, CancellationToken ct = default)
    {
        await using var s = (Session)await BeginAsync(ct);
        await s.Records.DeleteAsync(entity, id, ct);
        await s.CommitAsync(ct);
    }

    public RecordSnapshot? PeekRecord(string entity, string id)
    {
        lock (Gate)
        {
            return Tables.TryGetValue("records", out var t) && t.TryGetValue($"{entity}:{id}", out var r) ? Clone((StoredRecord)r).ToSnapshot() : null;
        }
    }

    public IReadOnlyList<T> All<T>(string table) where T : class
    {
        lock (Gate)
        {
            return Tables.TryGetValue(table, out var t) ? t.Values.Cast<T>().Select(Clone).ToList() : [];
        }
    }

    internal static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, value.GetType(), CloneOptions), CloneOptions)!;

    private static readonly JsonSerializerOptions CloneOptions = new() { IncludeFields = false };

    internal sealed class StoredRecord
    {
        public string Entity { get; set; } = "";
        public string Id { get; set; } = "";
        public long Order { get; set; }
        public string Json { get; set; } = "{}";

        public RecordSnapshot ToSnapshot() => new()
        {
            Entity = Entity, Id = Id, Values = (Dictionary<string, object?>)(Values.FromJson(Json) ?? new Dictionary<string, object?>()),
        };
    }

    private sealed class TriggerClaim
    {
        public string Key { get; set; } = "";
    }

    /// <summary>One unit of work: writes go to an overlay until commit.</summary>
    internal sealed class Session : IWorkflowSession
    {
        private readonly InMemoryWorkflowStore _store;
        private readonly Dictionary<(string Table, string Key), object?> _overlay = [];
        private bool _done;

        public Session(InMemoryWorkflowStore store)
        {
            _store = store;
            Records = new Gateway(this);
        }

        public Gateway Records { get; }
        public IEntityGateway Entities => Records;
        internal InMemoryWorkflowStore Store => _store;

        // ── Generic table access ──────────────────────────────────────────

        internal T? Get<T>(string table, string key) where T : class
        {
            if (_overlay.TryGetValue((table, key), out var o)) return o is null ? null : Clone((T)o);
            lock (_store.Gate)
            {
                return _store.Tables.TryGetValue(table, out var t) && t.TryGetValue(key, out var v) ? Clone((T)v) : null;
            }
        }

        internal List<T> All<T>(string table) where T : class
        {
            var result = new Dictionary<string, T>();
            lock (_store.Gate)
            {
                if (_store.Tables.TryGetValue(table, out var t))
                    foreach (var (k, v) in t) result[k] = (T)v;
            }
            foreach (var ((tb, k), v) in _overlay)
            {
                if (tb != table) continue;
                if (v is null) result.Remove(k);
                else result[k] = (T)v;
            }
            return result.Values.Select(Clone).ToList();
        }

        internal void Put<T>(string table, string key, T value) where T : class => _overlay[(table, key)] = Clone(value);

        internal void Remove(string table, string key) => _overlay[(table, key)] = null;

        // ── Definitions ───────────────────────────────────────────────────

        public Task<DefinitionRecord?> GetDefinitionAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<DefinitionRecord>("definitions", id));
        public Task<DefinitionRecord?> GetDefinitionByKeyAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(All<DefinitionRecord>("definitions").FirstOrDefault(d => d.Key == key));
        public Task<IReadOnlyList<DefinitionRecord>> ListDefinitionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DefinitionRecord>>(All<DefinitionRecord>("definitions"));
        public Task AddDefinitionAsync(DefinitionRecord d, CancellationToken ct = default) { Put("definitions", d.Id, d); return Task.CompletedTask; }
        public Task UpdateDefinitionAsync(DefinitionRecord d, CancellationToken ct = default) { Put("definitions", d.Id, d); return Task.CompletedTask; }
        public Task DeleteDefinitionAsync(string id, CancellationToken ct = default) { Remove("definitions", id); return Task.CompletedTask; }

        public Task<DefinitionVersionRecord?> GetVersionAsync(string definitionId, int version, CancellationToken ct = default) =>
            Task.FromResult(Get<DefinitionVersionRecord>("versions", $"{definitionId}:{version}"));
        public Task<IReadOnlyList<DefinitionVersionRecord>> ListVersionsAsync(string definitionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DefinitionVersionRecord>>(All<DefinitionVersionRecord>("versions").Where(v => v.DefinitionId == definitionId).ToList());
        public Task AddVersionAsync(DefinitionVersionRecord v, CancellationToken ct = default) { Put("versions", $"{v.DefinitionId}:{v.Version}", v); return Task.CompletedTask; }

        public Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TriggerRecord>>(All<TriggerRecord>("triggers"));
        public Task ReplaceTriggersAsync(string definitionId, IReadOnlyList<TriggerRecord> triggers, CancellationToken ct = default)
        {
            foreach (var t in All<TriggerRecord>("triggers").Where(t => t.DefinitionId == definitionId)) Remove("triggers", t.Id);
            foreach (var t in triggers) Put("triggers", t.Id, t);
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimTriggerEventAsync(string definitionId, string eventId, CancellationToken ct = default)
        {
            var key = $"{definitionId}:{eventId}";
            if (Get<TriggerClaim>("claims", key) is not null) return Task.FromResult(false);
            Put("claims", key, new TriggerClaim { Key = key });
            return Task.FromResult(true);
        }

        // ── Instances, steps ──────────────────────────────────────────────

        public Task<InstanceRecord?> GetInstanceAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<InstanceRecord>("instances", id));

        private IEnumerable<InstanceRecord> Filter(InstanceQuery q) => All<InstanceRecord>("instances").Where(i =>
            (q.DefinitionId is null || i.DefinitionId == q.DefinitionId) && (q.DefinitionKey is null || i.DefinitionKey == q.DefinitionKey) &&
            (q.Entity is null || i.Entity == q.Entity) && (q.EntityId is null || i.EntityId == q.EntityId) &&
            (q.Statuses is null || q.Statuses.Contains(i.Status)) && (q.Kinds is null || q.Kinds.Contains(i.Kind)) && (q.ParentId is null || i.ParentId == q.ParentId));

        public Task<IReadOnlyList<InstanceRecord>> QueryInstancesAsync(InstanceQuery q, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstanceRecord>>(Filter(q).OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Id).Skip(q.Skip).Take(q.Take).ToList());
        public Task<int> CountInstancesAsync(InstanceQuery q, CancellationToken ct = default) => Task.FromResult(Filter(q).Count());
        public Task AddInstanceAsync(InstanceRecord i, CancellationToken ct = default) { Put("instances", i.Id, i); return Task.CompletedTask; }
        public Task UpdateInstanceAsync(InstanceRecord i, CancellationToken ct = default) { Put("instances", i.Id, i); return Task.CompletedTask; }

        public Task AddStepAsync(StepRecord s, CancellationToken ct = default) { Put("steps", s.Id, s); return Task.CompletedTask; }
        public Task UpdateStepAsync(StepRecord s, CancellationToken ct = default) { Put("steps", s.Id, s); return Task.CompletedTask; }
        public Task<IReadOnlyList<StepRecord>> ListStepsAsync(string instanceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StepRecord>>(All<StepRecord>("steps").Where(s => s.InstanceId == instanceId).OrderBy(s => s.StartedAt).ToList());

        // ── Bookmarks ─────────────────────────────────────────────────────

        public Task AddBookmarkAsync(BookmarkRecord b, CancellationToken ct = default) { Put("bookmarks", b.Id, b); return Task.CompletedTask; }
        public Task RemoveBookmarkAsync(string id, CancellationToken ct = default) { Remove("bookmarks", id); return Task.CompletedTask; }
        public Task<BookmarkRecord?> GetBookmarkAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<BookmarkRecord>("bookmarks", id));
        public Task<IReadOnlyList<BookmarkRecord>> ListBookmarksAsync(string instanceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BookmarkRecord>>(All<BookmarkRecord>("bookmarks").Where(b => b.InstanceId == instanceId).ToList());

        public Task<IReadOnlyList<BookmarkRecord>> ClaimDueBookmarksAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default)
        {
            var due = All<BookmarkRecord>("bookmarks")
                .Where(b => b.DueAt is not null && b.DueAt <= now && (b.LockedUntil is null || b.LockedUntil <= now))
                .OrderBy(b => b.DueAt).ThenBy(b => b.CreatedAt).Take(limit).ToList();
            foreach (var b in due)
            {
                b.LockedUntil = lockUntil;
                Put("bookmarks", b.Id, b);
            }
            return Task.FromResult<IReadOnlyList<BookmarkRecord>>(due);
        }

        public Task<IReadOnlyList<BookmarkRecord>> FindBookmarksAsync(string kind, string? entity, string? entityId, string? signal, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BookmarkRecord>>(All<BookmarkRecord>("bookmarks").Where(b => b.Kind == kind &&
                (entity is null || b.Entity == entity) && (entityId is null || b.EntityId == entityId) && (signal is null || b.Signal == signal)).OrderBy(b => b.CreatedAt).ToList());

        public Task<IReadOnlyList<BookmarkRecord>> FindDefinitionBookmarksAsync(string definitionId, string kind, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BookmarkRecord>>(All<BookmarkRecord>("bookmarks").Where(b => b.DefinitionId == definitionId && b.Kind == kind && b.InstanceId is null).ToList());

        // ── Tasks ─────────────────────────────────────────────────────────

        public Task<TaskRecord?> GetTaskAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<TaskRecord>("tasks", id));

        private IEnumerable<TaskRecord> Filter(TaskQuery q) => All<TaskRecord>("tasks").Where(t =>
        {
            if (q.InstanceId is not null && t.InstanceId != q.InstanceId) return false;
            if (q.Entity is not null && t.Entity != q.Entity) return false;
            if (q.EntityId is not null && t.EntityId != q.EntityId) return false;
            if (q.Statuses is not null && !q.Statuses.Contains(t.Status)) return false;
            if (q.AssigneeId is not null || q.AssigneeAnyOf is not null)
            {
                var people = (Values.FromJson(t.Assignees) as List<object?> ?? []).Select(Values.Text).ToHashSet();
                if (q.AssigneeId is not null && !people.Contains(q.AssigneeId)) return false;
                if (q.AssigneeAnyOf is not null && !q.AssigneeAnyOf.Any(people.Contains)) return false;
            }
            return true;
        });

        public Task<IReadOnlyList<TaskRecord>> QueryTasksAsync(TaskQuery q, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TaskRecord>>(Filter(q).OrderBy(t => t.Status == TaskStatus.Open ? 0 : 1).ThenBy(t => t.DueAt).Skip(q.Skip).Take(q.Take).ToList());
        public Task<int> CountTasksAsync(TaskQuery q, CancellationToken ct = default) => Task.FromResult(Filter(q).Count());
        public Task AddTaskAsync(TaskRecord t, CancellationToken ct = default) { Put("tasks", t.Id, t); return Task.CompletedTask; }
        public Task UpdateTaskAsync(TaskRecord t, CancellationToken ct = default) { Put("tasks", t.Id, t); return Task.CompletedTask; }

        // ── Outbox ────────────────────────────────────────────────────────

        public Task AddOutboxAsync(OutboxRecord o, CancellationToken ct = default) { Put("outbox", o.Id, o); return Task.CompletedTask; }
        public Task UpdateOutboxAsync(OutboxRecord o, CancellationToken ct = default) { Put("outbox", o.Id, o); return Task.CompletedTask; }

        public Task<IReadOnlyList<OutboxRecord>> ClaimOutboxAsync(DateTimeOffset now, DateTimeOffset lockUntil, int limit, CancellationToken ct = default)
        {
            var rows = All<OutboxRecord>("outbox").Where(o => o.ProcessedAt is null && (o.LockedUntil is null || o.LockedUntil <= now))
                .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Take(limit).ToList();
            foreach (var o in rows)
            {
                o.LockedUntil = lockUntil;
                Put("outbox", o.Id, o);
            }
            return Task.FromResult<IReadOnlyList<OutboxRecord>>(rows);
        }

        // ── Audit ─────────────────────────────────────────────────────────

        public Task<AuditRecord?> GetAuditHeadAsync(string? tenant, CancellationToken ct = default) =>
            Task.FromResult(All<AuditRecord>("audit").Where(a => a.Tenant == tenant).OrderByDescending(a => a.Seq).FirstOrDefault());

        public Task AddAuditAsync(AuditRecord a, CancellationToken ct = default) { Put("audit", a.Seq.ToString("D19", System.Globalization.CultureInfo.InvariantCulture), a); return Task.CompletedTask; }

        public Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default)
        {
            var rows = All<AuditRecord>("audit").Where(a =>
                (q.Category is null || a.Category == q.Category) && (q.Action is null || a.Action == q.Action) && (q.Entity is null || a.Entity == q.Entity) &&
                (q.EntityId is null || a.EntityId == q.EntityId) && (q.InstanceId is null || a.InstanceId == q.InstanceId || a.CorrelationId == q.InstanceId) &&
                (q.DefinitionId is null || a.DefinitionId == q.DefinitionId) && (q.ActorId is null || a.ActorId == q.ActorId) &&
                (q.From is null || a.At >= q.From) && (q.To is null || a.At < q.To) && (q.Before is null || a.Seq < q.Before) && (q.FromSeq is null || a.Seq >= q.FromSeq));
            var ordered = q.FromSeq is not null ? rows.OrderBy(a => a.Seq) : rows.OrderByDescending(a => a.Seq);
            return Task.FromResult<IReadOnlyList<AuditRecord>>(ordered.Take(q.Take).ToList());
        }

        public Task<int> PurgeAuditAsync(string category, DateTimeOffset before, CancellationToken ct = default)
        {
            var victims = All<AuditRecord>("audit").Where(a => a.Category == category && a.At < before).ToList();
            foreach (var v in victims) Remove("audit", v.Seq.ToString("D19", System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(victims.Count);
        }

        // ── Settings ──────────────────────────────────────────────────────

        public Task<SettingsRecord?> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Get<SettingsRecord>("settings", "global"));
        public Task SaveSettingsAsync(SettingsRecord s, CancellationToken ct = default) { Put("settings", "global", s); return Task.CompletedTask; }

        // ── Commit ────────────────────────────────────────────────────────

        public Task CommitAsync(CancellationToken ct = default)
        {
            if (_done) throw new InvalidOperationException("This session has already been committed.");
            lock (_store.Gate)
            {
                // Audit entries form one chain: refuse a commit whose chain head moved under us.
                var newAudit = _overlay.Where(kv => kv.Key.Table == "audit" && kv.Value is AuditRecord).Select(kv => (AuditRecord)kv.Value!).OrderBy(a => a.Seq).ToList();
                if (newAudit.Count > 0)
                {
                    var head = _store.Tables.TryGetValue("audit", out var at) && at.Count > 0 ? at.Values.Cast<AuditRecord>().Max(a => a.Seq) : 0;
                    if (newAudit[0].Seq != head + 1) throw new Engine.WorkflowConcurrencyException("The audit chain moved; retry.");
                }
                foreach (var ((table, key), value) in _overlay)
                {
                    if (!_store.Tables.TryGetValue(table, out var t)) _store.Tables[table] = t = [];
                    if (value is null) t.Remove(key);
                    else t[key] = value;
                }
            }
            _done = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records kept in memory, inspected like the EF interceptor does (change events, owned fields).</summary>
    internal sealed class Gateway(Session session) : IEntityGateway
    {
        private static string Key(string entity, string id) => $"{entity}:{id}";

        public async Task<RecordSnapshot?> GetAsync(string entity, string id, CancellationToken ct = default)
        {
            var r = session.Get<StoredRecord>("records", Key(entity, id));
            if (r is not null) return r.ToSnapshot();
            return session.Store.Fallback is null ? null : await session.Store.Fallback.GetAsync(entity, id, ct);
        }

        public async Task<IReadOnlyList<RecordSnapshot>> GetCollectionAsync(string entity, string id, string field, CancellationToken ct = default)
        {
            var owner = await GetAsync(entity, id, ct);
            if (owner is null) return [];
            var local = session.Get<StoredRecord>("records", Key(entity, id));
            if (local is null && session.Store.Fallback is not null) return await session.Store.Fallback.GetCollectionAsync(entity, id, field, ct);
            var related = session.Store.Registry?.Field(entity, field)?.Entity;
            var list = new List<RecordSnapshot>();
            foreach (var childId in owner[field] as List<object?> ?? [])
            {
                var cid = Values.Text(childId);
                var child = related is null ? null : await GetAsync(related, cid, ct);
                if (child is not null) list.Add(child);
            }
            return list;
        }

        public async Task SetFieldsAsync(string entity, string id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
        {
            var existing = session.Get<StoredRecord>("records", Key(entity, id));
            RecordSnapshot? before = existing?.ToSnapshot();
            if (before is null && session.Store.Fallback is not null) before = await session.Store.Fallback.GetAsync(entity, id, ct);
            if (before is null) throw new WorkflowException("noRecord", $"{entity} {id} does not exist.");
            var after = new Dictionary<string, object?>(before.Values, StringComparer.Ordinal);
            foreach (var (k, v) in values) after[k] = Values.Normalize(v);
            var result = session.Store.Changes?.Inspect(entity, id, ChangeKinds.Updated, before.Values, after);
            session.Put("records", Key(entity, id), new StoredRecord { Entity = entity, Id = id, Order = existing?.Order ?? Interlocked.Increment(ref session.Store.RecordOrder), Json = Values.Serialize(after) });
            if (result?.Change is { } change) await session.AddOutboxAsync(session.Store.Changes!.ToOutbox(change), ct);
        }

        public async Task CreateAsync(string entity, string id, Dictionary<string, object?> values, CancellationToken ct)
        {
            if (session.Get<StoredRecord>("records", Key(entity, id)) is not null) throw new WorkflowException("duplicateRecord", $"{entity} {id} already exists.");
            var after = values.ToDictionary(kv => kv.Key, kv => Values.Normalize(kv.Value), StringComparer.Ordinal);
            var result = session.Store.Changes?.Inspect(entity, id, ChangeKinds.Created, null, after);
            foreach (var (k, v) in result?.InitialValues ?? []) after[k] = v;
            session.Put("records", Key(entity, id), new StoredRecord { Entity = entity, Id = id, Order = Interlocked.Increment(ref session.Store.RecordOrder), Json = Values.Serialize(after) });
            if (result?.Change is { } change) await session.AddOutboxAsync(session.Store.Changes!.ToOutbox(change), ct);
        }

        public async Task DeleteAsync(string entity, string id, CancellationToken ct)
        {
            var existing = session.Get<StoredRecord>("records", Key(entity, id)) ?? throw new WorkflowException("noRecord", $"{entity} {id} does not exist.");
            var result = session.Store.Changes?.Inspect(entity, id, ChangeKinds.Deleted, existing.ToSnapshot().Values, null);
            session.Remove("records", Key(entity, id));
            if (result?.Change is { } change) await session.AddOutboxAsync(session.Store.Changes!.ToOutbox(change), ct);
        }

        public Task<IReadOnlyList<string>> ListIdsAsync(string entity, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(session.All<StoredRecord>("records").Where(r => r.Entity == entity).OrderByDescending(r => r.Order).Take(limit).Select(r => r.Id).ToList());
    }

    /// <summary>The registry, for resolving collection fields to their entity.</summary>
    public Registry.WorkflowRegistry? Registry { get; set; }
}
