using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

/// <summary>Raised by stores when a row changed under us (optimistic concurrency). The engine retries.</summary>
public sealed class WorkflowConcurrencyException(string message) : Exception(message);

/// <summary>
/// The workflow engine. Every public operation is one unit of work: workflow state, audit entries,
/// outbox rows and (with EF Core) the host's record changes commit together or not at all.
/// </summary>
public sealed partial class WorkflowEngine
{
    private readonly ConcurrentDictionary<(string, int), CompiledDefinition> _compiled = new();
    private volatile OwnershipMap? _ownership;

    /// <summary>The services and limits the engine was created with.</summary>
    public WorkflowEngineOptions Options { get; }
    /// <summary>What workflows may use (spec §2).</summary>
    public WorkflowRegistry Registry => Options.Registry;

    /// <summary>Creates an engine. Apps normally get it from DI (<c>AddWorkflows</c>); tests use <c>WorkflowTestHost</c>.</summary>
    public WorkflowEngine(WorkflowEngineOptions options)
    {
        Options = options;
        Changes = new ChangeInspector(this);
    }

    /// <summary>Turns record writes into change events and enforces lifecycle field ownership (used by gateways and the EF interceptor).</summary>
    public ChangeInspector Changes { get; }

    internal DateTimeOffset Now => Options.Time.GetUtcNow();

    // ── Units of work ─────────────────────────────────────────────────────

    internal sealed class Uow(WorkflowEngine engine, IWorkflowSession session, GlobalSettings settings, DateTimeOffset now, bool simulation)
    {
        public WorkflowEngine Engine { get; } = engine;
        public IWorkflowSession S { get; } = session;
        public GlobalSettings Settings { get; } = settings;
        public WorkCalendar Calendar { get; } = settings.WorkCalendar(m => engine.Options.Logger.LogWarning("{Problem}", m));
        public DateTimeOffset Now { get; } = now;
        public string Today => Calendar.Today(Now);
        public AuditWriter Audit { get; } = new(session, engine.Options.Tenant, () => engine.Now);
        public bool Simulation { get; } = simulation;
        public SimulationLog? Log { get; init; }
        /// <summary>Instances created in this unit that still have to run (sub-workflows start after their parent is saved).</summary>
        public List<string> Deferred { get; } = [];
        /// <summary>Instances that finished in this unit, so queued instances can start after them.</summary>
        public List<InstanceRecord> Finished { get; } = [];
        /// <summary>
        /// Every instance loaded in this unit, by id. An instance is only ever held once per unit, so code that
        /// reaches the same instance twice (a run cancelling itself, a child resuming its parent) shares one copy.
        /// </summary>
        public Dictionary<string, Exec> Execs { get; } = [];
        /// <summary>Instances whose steps are executing right now (a run never cancels itself while it does).</summary>
        public HashSet<string> Driving { get; } = [];
        /// <summary>Lifecycle record instances loaded in this unit, by id (shared like <see cref="Execs"/>).</summary>
        public Dictionary<string, InstanceRecord> Records { get; } = [];
        /// <summary>Records this unit pinned to a lifecycle (or unpinned: null), applied to the ownership cache once it commits.</summary>
        public List<(string Entity, string Field, string Id, string? DefinitionId)> Pins { get; } = [];

        public ValueScope Scope(InstanceRecord? i = null, RunState? state = null, Token? token = null, TriggerInfo? trigger = null, string? entity = null, string? entityId = null)
        {
            var (item, itemIndex) = token?.CurrentItem() ?? (null, null);
            string? itemEntity = null;
            if (item is not null) itemEntity = item.GetValueOrDefault("$entity") as string;
            return new ValueScope(S.Entities, Engine.Registry)
            {
                Entity = entity ?? i?.Entity,
                EntityId = entityId ?? i?.EntityId,
                EntityOverride = state?.Snapshot,
                Zone = Settings.Zone(),
                Trigger = trigger ?? (i is null ? null : TriggerInfo.FromJson(i.TriggerJson)),
                State = state,
                Item = item,
                ItemIndex = itemIndex,
                ItemEntity = itemEntity,
                Instance = i,
                Now = Now,
                Today = Today,
            };
        }

        private readonly Dictionary<string, Masker> _maskers = [];

        /// <summary>Queues an audit entry (masked, spec §11); the chain is linked when the unit commits.</summary>
        public async Task AuditAsync(AuditEntry e, CancellationToken ct)
        {
            var m = await MaskerAsync(e.Entity, e.EntityId, ct);
            var data = m.Mask(e.Data);
            if (data is Dictionary<string, object?> d && d.TryGetValue("trigger", out var trig)) d["trigger"] = m.MaskTrigger(e.Data is Dictionary<string, object?> raw ? raw["trigger"] : trig);
            Audit.Write(e with { Before = m.Mask(e.Before), After = m.Mask(e.After), Data = data });
        }

        /// <summary>The masker for one record (its current sensitive values), cached for the unit.</summary>
        public async Task<Masker> MaskerAsync(string? entity, string? entityId, CancellationToken ct)
        {
            var key = $"{entity}\n{entityId}";
            if (_maskers.TryGetValue(key, out var m)) return m;
            IReadOnlyDictionary<string, object?>? values = null;
            if (entity is not null && entityId is not null && Engine.Registry.Entity(entity) is not null)
            {
                try
                {
                    values = (await S.Entities.GetAsync(entity, entityId, ct))?.Values;
                }
                catch (WorkflowException)
                {
                    // Records the gateway cannot read are masked by key only.
                }
            }
            return _maskers[key] = Masker.Create(Engine.Registry, Settings, values, entity);
        }

        /// <summary>Forgets a record's cached sensitive values (after a workflow wrote to it).</summary>
        public void ForgetMasker(string entity, string entityId) => _maskers.Remove($"{entity}\n{entityId}");
    }

    internal async Task<Uow> OpenAsync(IWorkflowSession session, CancellationToken ct)
    {
        var settings = GlobalSettings.Parse((await session.GetSettingsAsync(ct))?.Json);
        return new Uow(this, session, settings, Now, Options.SimulationLog is not null) { Log = Options.SimulationLog };
    }

    /// <summary>Runs one unit of work, retrying when a concurrent writer changed the same rows.</summary>
    internal async Task<T> RunAsync<T>(Func<Uow, Task<T>> work, CancellationToken ct)
    {
        await RefreshOwnershipAsync(ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var session = await Options.Store.BeginAsync(ct);
                // App services called during this unit (actions, identity…) come from the session's DI scope.
                var previous = DependencyInjection.WorkflowServiceScope.Current;
                DependencyInjection.WorkflowServiceScope.Current = session.Services ?? previous;
                try
                {
                    var uow = await OpenAsync(session, ct);
                    var result = await work(uow);
                    await RunDeferredAsync(uow, ct);
                    await StartQueuedAfterAsync(uow, ct);
                    await uow.Audit.FlushAsync(ct);
                    await session.CommitAsync(ct);
                    ApplyPins(uow);
                    return result;
                }
                finally
                {
                    DependencyInjection.WorkflowServiceScope.Current = previous;
                }
            }
            catch (WorkflowConcurrencyException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }
    }

    internal async Task RunDeferredAsync(Uow u, CancellationToken ct)
    {
        while (u.Deferred.Count > 0)
        {
            var id = u.Deferred[0];
            u.Deferred.RemoveAt(0);
            var x = await LoadExecAsync(u, id, ct);
            if (x.I.Status == InstanceStatus.Running) await DriveAsync(x, ct);
        }
    }

    internal Task RunAsync(Func<Uow, Task> work, CancellationToken ct) =>
        RunAsync<bool>(async u => { await work(u); return true; }, ct);

    internal async Task<CompiledDefinition> CompiledAsync(Uow u, string definitionId, int version, CancellationToken ct)
    {
        if (_compiled.TryGetValue((definitionId, version), out var c)) return c;
        var v = await u.S.GetVersionAsync(definitionId, version, ct)
                ?? throw new WorkflowException("definitionNotFound", $"Version {version} of workflow {definitionId} does not exist.");
        c = new CompiledDefinition(Definitions.Parse(v.Json), definitionId, version);
        _compiled[(definitionId, version)] = c;
        return c;
    }

    internal static string NewId() => Guid.NewGuid().ToString("N");

    // ── Lifecycle ownership (for the change hook) ─────────────────────────

    internal sealed record OwnedField(string DefinitionId, string DefinitionKey, string Entity, string Field, bool Enforce, string? InitialState, Condition? AppliesWhen,
        DateTimeOffset PublishedAt);

    /// <summary>
    /// The order lifecycles sharing a field are tried in (spec §6 applies to): those with a condition first,
    /// in publish order (first publish time, then definition id), then the fallback.
    /// </summary>
    internal static IEnumerable<T> InSelectionOrder<T>(IEnumerable<T> items, Func<T, bool> conditional, Func<T, DateTimeOffset> publishedAt, Func<T, string> definitionId) =>
        items.OrderBy(x => conditional(x) ? 0 : 1).ThenBy(publishedAt).ThenBy(definitionId, StringComparer.Ordinal);

    /// <summary>
    /// The lifecycle-owned fields and, for fields where the lifecycle depends on the record (several
    /// lifecycles, or an "applies to" condition), which lifecycle each record is pinned to. The change hook
    /// reads it synchronously while the app saves, so it never queries the store itself.
    /// </summary>
    internal sealed class OwnershipMap
    {
        private readonly HashSet<(string Entity, string Field)> _pinnedPairs;
        private readonly ConcurrentDictionary<(string Entity, string Field, string Id), string> _pins;

        public OwnershipMap(IReadOnlyList<OwnedField> fields, DateTimeOffset loadedAt, WorkCalendar calendar,
            HashSet<(string, string)>? pinnedPairs = null, ConcurrentDictionary<(string, string, string), string>? pins = null)
        {
            Fields = InSelectionOrder(fields, f => f.AppliesWhen is not null, f => f.PublishedAt, f => f.DefinitionId).ToList();
            LoadedAt = loadedAt;
            Calendar = calendar;
            _pinnedPairs = pinnedPairs ?? [];
            _pins = pins ?? new();
        }

        /// <summary>The owned fields, in selection order.</summary>
        public IReadOnlyList<OwnedField> Fields { get; }
        public DateTimeOffset LoadedAt { get; }
        /// <summary>The settings' calendar, so "today" in a condition means the same day as in processing.</summary>
        public WorkCalendar Calendar { get; }

        /// <summary>The same map, marked stale (the pins are kept).</summary>
        public OwnershipMap Stale() => new(Fields, DateTimeOffset.MinValue, Calendar, _pinnedPairs, _pins);

        /// <summary>Records a record's lifecycle for a field (null: the record has none any more).</summary>
        public void Pin(string entity, string field, string id, string? definitionId)
        {
            if (!_pinnedPairs.Contains((entity, field))) return;
            if (definitionId is null) _pins.TryRemove((entity, field, id), out _);
            else _pins[(entity, field, id)] = definitionId;
        }

        /// <summary>
        /// The lifecycle that controls <paramref name="field"/> of a record (spec §6 applies to): the one the
        /// record is pinned to (none while that lifecycle is archived); for a record with no lifecycle yet,
        /// the first whose condition its values meet.
        /// </summary>
        public OwnedField? For(string entity, string field, string id, IReadOnlyDictionary<string, object?>? values, string today)
        {
            if (_pinnedPairs.Contains((entity, field)) && _pins.TryGetValue((entity, field, id), out var pinned))
                return Fields.FirstOrDefault(f => f.DefinitionId == pinned && f.Entity == entity && f.Field == field);
            return Fields.Where(f => f.Entity == entity && f.Field == field).FirstOrDefault(f => LifecycleSelection.Applies(f.AppliesWhen, values, today));
        }
    }

    /// <summary>
    /// The lifecycle-owned fields, as last loaded. Every unit of work refreshes a stale copy before it
    /// opens its session (see <see cref="RefreshOwnershipAsync"/>); only the very first use loads it here,
    /// synchronously, because the change hook can run inside a synchronous SaveChanges.
    /// </summary>
    internal OwnershipMap Ownership
    {
        get
        {
            var m = _ownership;
            if (m is not null) return m;
            m = Task.Run(() => LoadOwnershipAsync(CancellationToken.None)).GetAwaiter().GetResult();
            _ownership = m;
            return m;
        }
    }

    /// <summary>Reloads the lifecycle-owned fields when the cached copy is older than <see cref="WorkflowEngineOptions.OwnershipRefreshInterval"/>.</summary>
    public async Task RefreshOwnershipAsync(CancellationToken ct = default)
    {
        var m = _ownership;
        if (m is not null && Now - m.LoadedAt < Options.OwnershipRefreshInterval && Now >= m.LoadedAt) return;
        _ownership = await LoadOwnershipAsync(ct);
    }

    /// <summary>Pin changes this engine committed, numbered, so a reload that read the store before they committed still gets them.</summary>
    private readonly List<(long Seq, string Entity, string Field, string Id, string? DefinitionId)> _pinLog = [];
    private long _pinSeq;

    internal async Task<OwnershipMap> LoadOwnershipAsync(CancellationToken ct)
    {
        long seenSeq;
        lock (_pinLog) seenSeq = _pinSeq;
        await using var s = await Options.Store.BeginAsync(ct);
        var triggers = await s.ListTriggersAsync(ct);
        var list = triggers.Where(t => t.Type == "lifecycle" && t.Entity is not null && t.Field is not null).Select(t =>
        {
            var data = Values.FromJson(t.Data ?? "{}") as Dictionary<string, object?> ?? [];
            return new OwnedField(t.DefinitionId, t.DefinitionKey, t.Entity!, t.Field!, data.GetValueOrDefault("mode") as string != "observe",
                data.GetValueOrDefault("initial") as string, AppliesWhenOf(t), t.PublishedAt);
        }).ToList();
        var calendar = GlobalSettings.Parse((await s.GetSettingsAsync(ct))?.Json).WorkCalendar(m => Options.Logger.LogWarning("{Problem}", m));

        // Pins matter where the lifecycle depends on the record: a field shared by several lifecycles
        // (archived ones included, their records stay pinned) or one with a condition.
        var lifecycles = new List<CompiledDefinition>();
        foreach (var d in await s.ListDefinitionsAsync(ct))
        {
            if (d.Kind != WorkflowKinds.Lifecycle || d.Version == 0) continue;
            if (!_compiled.TryGetValue((d.Id, d.Version), out var c))
            {
                var v = await s.GetVersionAsync(d.Id, d.Version, ct);
                if (v is null) continue;
                c = _compiled[(d.Id, d.Version)] = new CompiledDefinition(Definitions.Parse(v.Json), d.Id, d.Version);
            }
            if (c.Definition.Entity is not null && c.Definition.Lifecycle is not null) lifecycles.Add(c);
        }
        var pairs = new HashSet<(string, string)>();
        var pins = new ConcurrentDictionary<(string, string, string), string>();
        foreach (var g in lifecycles.GroupBy(c => (c.Definition.Entity!, c.Lifecycle.Field)))
        {
            if (g.Count() < 2 && g.All(c => c.Lifecycle.AppliesWhen is null)) continue;
            pairs.Add(g.Key);
            foreach (var c in g)
            {
                for (var skip = 0; ; skip += 1000)
                {
                    var page = await s.QueryInstancesAsync(new InstanceQuery
                    {
                        DefinitionId = c.DefinitionId, Kinds = [InstanceKinds.Record], Statuses = [InstanceStatus.Active, InstanceStatus.OutOfSync, InstanceStatus.Completed],
                        Skip = skip, Take = 1000,
                    }, ct);
                    foreach (var i in page)
                        if (i.EntityId is not null) pins.TryAdd((g.Key.Item1, g.Key.Item2, i.EntityId), c.DefinitionId);
                    if (page.Count < 1000) break;
                }
            }
        }
        var map = new OwnershipMap(list, Now, calendar, pairs, pins);
        lock (_pinLog)
        {
            foreach (var p in _pinLog)
                if (p.Seq > seenSeq) map.Pin(p.Entity, p.Field, p.Id, p.DefinitionId);
            _pinLog.RemoveAll(p => p.Seq <= seenSeq);
        }
        return map;
    }

    /// <summary>Applies the pins a committed unit changed to the cached ownership map.</summary>
    private void ApplyPins(Uow u)
    {
        if (u.Pins.Count == 0) return;
        lock (_pinLog)
        {
            foreach (var (entity, field, id, definitionId) in u.Pins)
            {
                _pinLog.Add((++_pinSeq, entity, field, id, definitionId));
                _ownership?.Pin(entity, field, id, definitionId);
            }
        }
    }

    /// <summary>Marks the lifecycle ownership cache stale; the next unit of work reloads it.</summary>
    public void InvalidateOwnership()
    {
        if (_ownership is { } m) _ownership = m.Stale();
    }

    internal WorkflowActor? CurrentActor() => Options.ActorAccessor?.Current;

    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _backgroundWrites = [];

    /// <summary>Waits for audit entries written in the background (refused writes). Tests and shutdown use it.</summary>
    public async Task FlushBackgroundAsync()
    {
        while (_backgroundWrites.TryTake(out var t)) await t;
    }

    /// <summary>Audits a refused write to a lifecycle-owned field, in its own unit of work (the refused write rolls back).</summary>
    internal void RecordRefusedWrite(string entity, string id, string field, string definitionId, object? from, object? to, Changes.WorkflowWriteScope? scope)
    {
        var actor = scope?.Actor ?? CurrentActor() ?? WorkflowActor.System;
        // Written in its own unit after the refused write rolls back; not awaited, so a database that
        // locks on writes (SQLite) is not asked to wait on itself.
        var pending = Task.Run(() => RunAsync(async u => await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "writeRefused", SubjectType = "record", SubjectId = id, Entity = entity, EntityId = id, DefinitionId = definitionId,
            Actor = actor, Before = new Dictionary<string, object?> { [field] = from }, After = new Dictionary<string, object?> { [field] = to },
        }, CancellationToken.None), CancellationToken.None));
        _backgroundWrites.Add(pending);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    /// <summary>The global settings (spec §12) and their revision, for optimistic saving.</summary>
    public Task<(GlobalSettings Settings, int Revision)> GetSettingsAsync(CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var rec = await u.S.GetSettingsAsync(ct);
            return (GlobalSettings.Parse(rec?.Json), rec?.Revision ?? 0);
        }, ct);

    /// <summary>
    /// Saves the global settings when <paramref name="expectedRevision"/> is still current and returns the new
    /// revision. Refuses <c>invalidSettings</c> (bad time zone, hours, durations, connection addresses) and
    /// <c>staleSettings</c> (someone saved meanwhile); <see cref="GlobalSettings.Problems"/> lists every check. Audited as <c>settings.updated</c> with before and after.
    /// </summary>
    public async Task<int> SaveSettingsAsync(GlobalSettings settings, int expectedRevision, WorkflowActor actor, CancellationToken ct = default)
    {
        var revision = await RunAsync(async u =>
        {
            var problems = settings.Problems();
            if (problems.Count > 0) throw new WorkflowException("invalidSettings", string.Join(" ", problems), problems);
            var rec = await u.S.GetSettingsAsync(ct);
            var current = rec?.Revision ?? 0;
            if (current != expectedRevision) throw new WorkflowException("staleSettings", "Someone else changed the settings. Reload and try again.");
            var before = rec?.Json;
            var json = settings.ToJson();
            await u.S.SaveSettingsAsync(new SettingsRecord { Json = json, Revision = current + 1, UpdatedAt = u.Now, UpdatedBy = actor.Id }, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "settings", Action = "updated", SubjectType = "settings", SubjectId = "global", Actor = actor,
                Before = before is null ? null : Values.FromJson(before), After = Values.FromJson(json),
            }, ct);
            return current + 1;
        }, ct);
        // The change hook reads "today" in the settings' time zone.
        InvalidateOwnership();
        return revision;
    }
}
