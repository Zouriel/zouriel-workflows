using System.Collections.Concurrent;
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

    public WorkflowEngineOptions Options { get; }
    public WorkflowRegistry Registry => Options.Registry;

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
        public WorkCalendar Calendar { get; } = settings.WorkCalendar();
        public DateTimeOffset Now { get; } = now;
        public string Today => Calendar.Today(Now);
        public AuditWriter Audit { get; } = new(session, engine.Options.Tenant, () => engine.Now);
        public bool Simulation { get; } = simulation;
        public SimulationLog? Log { get; init; }
        /// <summary>Instances created in this unit that still have to run (sub-workflows start after their parent is saved).</summary>
        public List<string> Deferred { get; } = [];
        /// <summary>Instances that finished in this unit, so queued instances can start after them.</summary>
        public List<InstanceRecord> Finished { get; } = [];
        public int StepBudget { get; set; } = engine.Options.MaxStepsPerRun;

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

        public Task<AuditRecord> AuditAsync(AuditEntry e, CancellationToken ct) => Audit.WriteAsync(e, ct);
    }

    internal async Task<Uow> OpenAsync(IWorkflowSession session, CancellationToken ct)
    {
        var settings = GlobalSettings.Parse((await session.GetSettingsAsync(ct))?.Json);
        return new Uow(this, session, settings, Now, Options.SimulationLog is not null) { Log = Options.SimulationLog };
    }

    /// <summary>Runs one unit of work, retrying when a concurrent writer changed the same rows.</summary>
    internal async Task<T> RunAsync<T>(Func<Uow, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var session = await Options.Store.BeginAsync(ct);
                var uow = await OpenAsync(session, ct);
                var result = await work(uow);
                await RunDeferredAsync(uow, ct);
                await StartQueuedAfterAsync(uow, ct);
                await session.CommitAsync(ct);
                return result;
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

    internal sealed record OwnedField(string DefinitionId, string DefinitionKey, string Entity, string Field, bool Enforce, string? InitialState);

    internal sealed class OwnershipMap(IReadOnlyList<OwnedField> fields, DateTimeOffset loadedAt)
    {
        public DateTimeOffset LoadedAt { get; } = loadedAt;
        public OwnedField? Find(string entity, string field) => fields.FirstOrDefault(f => f.Entity == entity && f.Field == field);
        public IEnumerable<OwnedField> ForEntity(string entity) => fields.Where(f => f.Entity == entity);
    }

    /// <summary>How long the change hook trusts its cached list of lifecycle-owned fields (other nodes may publish).</summary>
    public TimeSpan OwnershipRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    internal OwnershipMap Ownership
    {
        get
        {
            var m = _ownership;
            if (m is not null && Now - m.LoadedAt < OwnershipRefreshInterval) return m;
            // The change hook can run inside a synchronous SaveChanges, so this load is synchronous
            // on purpose; it happens at most once per refresh interval.
            m = LoadOwnershipAsync(CancellationToken.None).GetAwaiter().GetResult();
            _ownership = m;
            return m;
        }
    }

    internal async Task<OwnershipMap> LoadOwnershipAsync(CancellationToken ct)
    {
        await using var s = await Options.Store.BeginAsync(ct);
        var triggers = await s.ListTriggersAsync(ct);
        var list = triggers.Where(t => t.Type == "lifecycle" && t.Entity is not null && t.Field is not null).Select(t =>
        {
            var data = Values.FromJson(t.Data ?? "{}") as Dictionary<string, object?> ?? [];
            return new OwnedField(t.DefinitionId, t.DefinitionKey, t.Entity!, t.Field!, data.GetValueOrDefault("mode") as string != "observe",
                data.GetValueOrDefault("initial") as string);
        }).ToList();
        return new OwnershipMap(list, Now);
    }

    /// <summary>Forces the lifecycle ownership cache to reload (called after publishing).</summary>
    public void InvalidateOwnership() => _ownership = null;

    internal WorkflowActor? CurrentActor() => Options.ActorAccessor?.Current;

    /// <summary>Audits a refused write to a lifecycle-owned field, in its own unit of work (the refused write rolls back).</summary>
    internal void RecordRefusedWrite(string entity, string id, string field, string definitionId, object? from, object? to, Changes.WorkflowWriteScope? scope)
    {
        var actor = scope?.Actor ?? CurrentActor() ?? WorkflowActor.System;
        Task.Run(() => RunAsync(async u => await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "writeRefused", SubjectType = "record", SubjectId = id, Entity = entity, EntityId = id, DefinitionId = definitionId,
            Actor = actor, Before = new Dictionary<string, object?> { [field] = from }, After = new Dictionary<string, object?> { [field] = to },
        }, CancellationToken.None), CancellationToken.None)).GetAwaiter().GetResult();
    }

    // ── Settings ──────────────────────────────────────────────────────────

    public Task<(GlobalSettings Settings, int Revision)> GetSettingsAsync(CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var rec = await u.S.GetSettingsAsync(ct);
            return (GlobalSettings.Parse(rec?.Json), rec?.Revision ?? 0);
        }, ct);

    public Task<int> SaveSettingsAsync(GlobalSettings settings, int expectedRevision, WorkflowActor actor, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var problems = SettingsProblems(settings);
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

    private static List<string> SettingsProblems(GlobalSettings s)
    {
        var p = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.TimeZone))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                p.Add($"Unknown time zone '{s.TimeZone}'.");
            }
        }
        if (s.Calendar?.Hours is { Count: 2 } h && string.CompareOrdinal(h[0], h[1]) >= 0) p.Add("Working hours must end after they start.");
        if (s.Defaults?.TaskDue is { } due && !IsoDuration.TryParse(due, out _)) p.Add($"'{due}' is not a duration.");
        foreach (var c in s.Webhooks?.Connections ?? [])
            if (!Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) p.Add($"Connection '{c.Key}' needs an http(s) address.");
        return p;
    }
}
