using Zouriel.Workflows.Json;
using Zouriel.Workflows.Lint;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

/// <summary>One thing to do during a test run, after the workflow starts.</summary>
public sealed class SimulationEvent
{
    /// <summary><c>decide</c> (an open task), <c>advance</c> (move the clock), <c>signal</c>, <c>set</c> (change record fields).</summary>
    public string Type { get; set; } = "";
    /// <summary>For <c>decide</c>: the step of the task (defaults to the first open task).</summary>
    public string? Step { get; set; }
    /// <summary>For <c>decide</c>: the outcome key (default <c>approved</c>).</summary>
    public string? Outcome { get; set; }
    /// <summary>For <c>decide</c>: the comment (default "Test run").</summary>
    public string? Comment { get; set; }
    /// <summary>For <c>decide</c>: the form answers; for <c>signal</c>: the signal data.</summary>
    public Dictionary<string, object?>? Data { get; set; }
    /// <summary>For <c>decide</c>: who decides (defaults to the task's first undecided assignee).</summary>
    public string? User { get; set; }
    /// <summary>For <c>advance</c>: an ISO duration.</summary>
    public string? Duration { get; set; }
    /// <summary>For <c>signal</c>: the signal key, raised for the test record.</summary>
    public string? Signal { get; set; }
    /// <summary>For <c>set</c>: the record fields to change (a committed change, so triggers and condition waits react).</summary>
    public Dictionary<string, object?>? Values { get; set; }
}

/// <summary>What a test run (<c>POST /simulate</c>) should do: which record to use, how the workflow starts and what happens next (at most 50 events).</summary>
public sealed class SimulationRequest
{
    /// <summary>The real record to read (its values are read, never written).</summary>
    public string? EntityId { get; set; }
    /// <summary>Record values to use instead of (or on top of) the real record's.</summary>
    public Dictionary<string, object?>? Values { get; set; }
    /// <summary>Automations: the change that starts it (<c>field</c>, <c>from</c>, <c>to</c>), or signal/manual data.</summary>
    public Dictionary<string, object?>? Trigger { get; set; }
    /// <summary>Lifecycles: the state the record is in, and the transition to try.</summary>
    public string? FromState { get; set; }
    /// <summary>Lifecycles: the transition key to apply (who is not checked in test runs).</summary>
    public string? Transition { get; set; }
    /// <summary>Form answers: the transition's input or the manual trigger's form.</summary>
    public Dictionary<string, object?>? Input { get; set; }
    /// <summary>What happens after the start, in order (at most <see cref="WorkflowEngine.MaxSimulationEvents"/>).</summary>
    public List<SimulationEvent>? Events { get; set; }
}

/// <summary>What a test run did. Everything is masked like the audit log; fields workflows may not read are left out.</summary>
/// <param name="Problems">Lint problems of the definition (errors stop the run before it starts).</param>
/// <param name="Log">What happened, side effects that were skipped included (messages, webhooks, actions, writes).</param>
/// <param name="Instances">The instances the run created, oldest first.</param>
/// <param name="OpenTasks">Tasks still open at the end.</param>
/// <param name="Record">The test record's values at the end (masked, readable fields only).</param>
/// <param name="Error">Why the run stopped early, if it did.</param>
public sealed record SimulationResult(
    IReadOnlyList<LintProblem> Problems, IReadOnlyList<SimulationEntry> Log, IReadOnlyList<InstanceView> Instances, IReadOnlyList<TaskView> OpenTasks,
    Dictionary<string, object?>? Record, string? Error);

public sealed partial class WorkflowEngine
{
    /// <summary>
    /// Runs a definition (usually the draft) against a real or made-up record without changing
    /// anything: records are read from the app, but writes, notifications, webhooks and actions stay
    /// inside the test run and are reported in its log.
    /// </summary>
    public async Task<SimulationResult> SimulateAsync(string definitionJson, SimulationRequest req, WorkflowActor actor, CancellationToken ct = default)
    {
        if (req.Events is { Count: > MaxSimulationEvents }) throw new WorkflowException("tooManyEvents", $"A test run can have at most {MaxSimulationEvents} events.");
        // Bad input is refused up front (not reported as a run that failed half way).
        foreach (var ev in req.Events ?? [])
            if (ev.Type == "advance") IsoDuration.Parse(ev.Duration ?? "PT1H");
        Normalize(req);
        var problems = await ValidateAsync(definitionJson, ct);
        if (problems.Any(p => p.Severity == "error")) return new SimulationResult(problems, [], [], [], null, "Fix the problems before testing.");
        var def = Definitions.Parse(definitionJson);
        var run = await CreateSimulationAsync(def, definitionJson, ct);
        string? error = null;
        try
        {
            if (def.Entity is not null && req.EntityId is not null && req.Values is { Count: > 0 })
            {
                var values = new Dictionary<string, object?>((await run.Store.Fallback!.GetAsync(def.Entity, req.EntityId, ct))?.Values ?? [], StringComparer.Ordinal);
                foreach (var (k, v) in req.Values) values[k] = Values.Normalize(v);
                await PutRecordDirectAsync(run.Store, def.Entity, req.EntityId, values, ct);
            }
            await run.Engine.RunAsync(u => StartSimulationAsync(run, u, def, req, actor, ct), ct);
            await DrainAsync(run.Engine, ct);
            foreach (var ev in req.Events ?? [])
            {
                await ApplySimulationEventAsync(run.Engine, run.Store, run.Clock, def, req, ev, ct);
                await DrainAsync(run.Engine, ct);
            }
        }
        catch (WorkflowException e)
        {
            error = e.Message;
            run.Log.Add("error", e.Code, null, e.Message);
        }
        return await CollectSimulationAsync(run, def, req, problems, error, ct);
    }

    /// <summary>A test run's private engine: an in-memory store over the app's records, a manual clock and a log of side effects.</summary>
    private sealed record SimulationRun(WorkflowEngine Engine, InMemoryWorkflowStore Store, ManualTimeProvider Clock, SimulationLog Log);

    private async Task<SimulationRun> CreateSimulationAsync(WorkflowDefinition def, string definitionJson, CancellationToken ct)
    {
        var clock = new ManualTimeProvider(Now);
        var log = new SimulationLog(clock.GetUtcNow);
        // Real records are read one short unit at a time, so a test run never holds a database transaction open.
        var store = new InMemoryWorkflowStore { Registry = Registry, Fallback = new PerReadGateway(Options.Store) };
        var sim = new WorkflowEngine(new WorkflowEngineOptions
        {
            Registry = Registry, Store = store, Identity = Options.Identity, Time = clock, Tenant = Options.Tenant, SimulationLog = log,
            FormValidator = Options.FormValidator, MaxStepsPerRun = Options.MaxStepsPerRun, NotifyTaskAssignees = Options.NotifyTaskAssignees,
            TaskNotificationChannel = Options.TaskNotificationChannel, RecordLinkTemplate = Options.RecordLinkTemplate,
        });
        store.Changes = sim.Changes;
        await using (var real = await Options.Store.BeginAsync(ct)) await SeedSimulationAsync(real, store, def, definitionJson, ct);
        return new SimulationRun(sim, store, clock, log);
    }

    /// <summary>Starts the definition under test: a lifecycle record (optionally applying a transition), or an automation's trigger.</summary>
    private static async Task StartSimulationAsync(SimulationRun run, Uow u, WorkflowDefinition def, SimulationRequest req, WorkflowActor actor, CancellationToken ct)
    {
        var sim = run.Engine;
        var c = await sim.CompiledAsync(u, def.Id, 1, ct);
        if (def.IsLifecycle)
        {
            if (req.EntityId is null) throw new WorkflowException("noRecord", "Pick a record to test the lifecycle with.");
            if ((req.FromState is null ? null : c.StateByKey(req.FromState)) is { } from)
            {
                var snap = await u.S.Entities.GetAsync(def.Entity!, req.EntityId, ct);
                var values = new Dictionary<string, object?>(snap?.Values ?? [], StringComparer.Ordinal) { [c.Lifecycle.Field] = from.Key };
                await PutRecordDirectAsync(run.Store, def.Entity!, req.EntityId, values, ct, u.S);
            }
            await sim.EnsureRecordInstanceAsync(u, c, def.Entity!, req.EntityId, ct, actor);
            if (req.Transition is not null)
                await sim.ApplyTransitionCoreAsync(u, def.Key, def.Entity!, req.EntityId, req.Transition, req.Input ?? [], actor, isPerson: false, auto: false, 0, ct);
            return;
        }
        var t = def.Trigger!;
        var data = req.Trigger ?? [];
        var info = new TriggerInfo
        {
            Type = t.Type, Field = t.Field, From = data.GetValueOrDefault("from"), To = data.GetValueOrDefault("to") ?? t.To?.Select(Values.FromJson).FirstOrDefault(),
            Transition = data.GetValueOrDefault("transition") as string, ActorType = actor.Type, ActorId = actor.Id, ActorName = actor.Name,
            At = Values.FormatInstant(u.Now), EventId = NewId(), Data = data.GetValueOrDefault("data") as Dictionary<string, object?>,
        };
        var entity = t.Type == TriggerTypes.Schedule && t.ForEach is null ? null : def.Entity;
        await sim.StartAutomationAsync(u, c, entity, entity is null ? null : req.EntityId, info, actor, 0, req.Input, null, null, null, ct);
    }

    /// <summary>
    /// What a test run shows: its instances, open tasks, the record and the log, masked like the audit log;
    /// fields workflows may not read are left out.
    /// </summary>
    private async Task<SimulationResult> CollectSimulationAsync(SimulationRun run, WorkflowDefinition def, SimulationRequest req, IReadOnlyList<LintProblem> problems,
        string? error, CancellationToken ct)
    {
        var instances = new List<InstanceView>();
        var tasks = new List<TaskView>();
        await run.Engine.RunAsync(async u =>
        {
            foreach (var i in (await u.S.QueryInstancesAsync(new InstanceQuery { Take = 1000 }, ct)).OrderBy(i => i.StartedAt))
                instances.Add(await run.Engine.InstanceViewAsync(u, i.Id, ct));
            foreach (var t in await u.S.QueryTasksAsync(new TaskQuery { Statuses = [Storage.TaskStatus.Open], Take = 1000 }, ct))
                tasks.Add(await run.Engine.TaskViewAsync(u, t, null, ct));
        }, ct);
        Dictionary<string, object?>? record = null;
        await using var view = await run.Store.BeginAsync(ct);
        var mask = await (await run.Engine.OpenAsync(view, ct)).MaskerAsync(def.Entity, req.EntityId, ct);
        if (def.Entity is not null && req.EntityId is not null && (await view.Entities.GetAsync(def.Entity, req.EntityId, ct))?.Values is { } current)
        {
            var readable = current.Where(kv => Registry.Field(def.Entity, kv.Key)?.IsReadable != false).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            record = mask.MaskMap(readable);
        }
        var entries = run.Log.Entries.Select(e => e with { Text = mask.MaskText(e.Text), Data = mask.Mask(e.Data) }).ToList();
        return new SimulationResult(problems, entries, instances, tasks, record, error);
    }

    /// <summary>Reads the app's records through a short unit of work per read (test runs never write to them).</summary>
    private sealed class PerReadGateway(IWorkflowStore store) : IEntityGateway
    {
        private async Task<T> ReadAsync<T>(Func<IEntityGateway, Task<T>> read, CancellationToken ct)
        {
            await using var s = await store.BeginAsync(ct);
            return await read(s.Entities);
        }

        public Task<RecordSnapshot?> GetAsync(string entity, string id, CancellationToken ct = default) => ReadAsync(g => g.GetAsync(entity, id, ct), ct);
        public Task<IReadOnlyList<RecordSnapshot>> GetCollectionAsync(string entity, string id, string field, CancellationToken ct = default) => ReadAsync(g => g.GetCollectionAsync(entity, id, field, ct), ct);
        public Task<IReadOnlyList<string>> ListIdsAsync(string entity, string? after, int limit, CancellationToken ct = default) => ReadAsync(g => g.ListIdsAsync(entity, after, limit, ct), ct);
        public Task SetFieldsAsync(string entity, string id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default) =>
            throw new InvalidOperationException("Test runs never write to the app's records.");
    }

    /// <summary>The most events one test run may replay.</summary>
    public const int MaxSimulationEvents = 50;

    /// <summary>Request bodies arrive with JSON elements inside; turn them into workflow values.</summary>
    private static void Normalize(SimulationRequest req)
    {
        static Dictionary<string, object?>? N(Dictionary<string, object?>? d) => d?.ToDictionary(kv => kv.Key, kv => Values.Normalize(kv.Value));
        req.Values = N(req.Values);
        req.Trigger = N(req.Trigger);
        req.Input = N(req.Input);
        foreach (var e in req.Events ?? [])
        {
            e.Data = N(e.Data);
            e.Values = N(e.Values);
        }
    }

    private static async Task DrainAsync(WorkflowEngine sim, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            var n = await sim.ProcessOutboxAsync(100, ct) + await sim.ProcessTimersAsync(100, ct);
            if (n == 0) return;
        }
    }

    private static async Task ApplySimulationEventAsync(WorkflowEngine sim, InMemoryWorkflowStore store, ManualTimeProvider clock, WorkflowDefinition def,
        SimulationRequest req, SimulationEvent ev, CancellationToken ct)
    {
        switch (ev.Type)
        {
            case "advance":
                clock.Advance(IsoDuration.Parse(ev.Duration ?? "PT1H").Approximate);
                sim.Options.SimulationLog?.Add("clock", "advanced", null, ev.Duration);
                break;
            case "decide":
                await sim.RunAsync(async u =>
                {
                    var open = await u.S.QueryTasksAsync(new TaskQuery { Statuses = [Storage.TaskStatus.Open], Take = 1000 }, ct);
                    var task = open.FirstOrDefault(t => ev.Step is null || t.NodeId == ev.Step) ?? throw new WorkflowException("noTask", "There is no open task to decide.");
                    var decided = ParseDecisions(task).Select(d => d.UserId).ToHashSet();
                    var userId = ev.User ?? (Values.FromJson(task.Assignees) as List<object?> ?? []).Select(Values.Text).FirstOrDefault(a => !decided.Contains(a))
                                 ?? throw new WorkflowException("noTask", "Everyone has decided this task.");
                    var user = await sim.Options.Identity.GetUserAsync(userId, ct);
                    await sim.DecideCoreAsync(u, task.Id, WorkflowActor.User(userId, user?.Name ?? userId), ev.Outcome ?? "approved", ev.Comment ?? "Test run", ev.Data, ct);
                }, ct);
                break;
            case "signal":
                await sim.SignalAsync(ev.Signal ?? "", def.Entity, req.EntityId, ev.Data, null, ct);
                break;
            case "set":
                if (def.Entity is not null && req.EntityId is not null && ev.Values is not null)
                {
                    await using var s = (InMemoryWorkflowStore.Session)await store.BeginAsync(ct);
                    await s.Records.SetFieldsAsync(def.Entity, req.EntityId, ev.Values, ct);
                    await s.CommitAsync(ct);
                }
                break;
        }
    }

    /// <summary>Copies what a test run needs: settings, published workflows (and lifecycles' triggers), and the definition under test as version 1.</summary>
    private static async Task SeedSimulationAsync(IWorkflowSession real, InMemoryWorkflowStore store, WorkflowDefinition def, string json, CancellationToken ct)
    {
        await using var s = await store.BeginAsync(ct);
        if (await real.GetSettingsAsync(ct) is { } settings) await s.SaveSettingsAsync(settings, ct);
        foreach (var d in await real.ListDefinitionsAsync(ct))
        {
            if (d.Id == def.Id || d.Version == 0 || d.Status == DefinitionStatus.Archived) continue;
            await s.AddDefinitionAsync(d, ct);
            var v = await real.GetVersionAsync(d.Id, d.Version, ct);
            if (v is not null) await s.AddVersionAsync(v, ct);
        }
        foreach (var t in await real.ListTriggersAsync(ct))
        {
            if (t.Type != "lifecycle" || t.DefinitionId == def.Id) continue;
            await s.ReplaceTriggersAsync(t.DefinitionId, [t], ct);
        }
        var now = DateTimeOffset.UtcNow;
        await s.AddDefinitionAsync(new DefinitionRecord
        {
            Id = def.Id, Key = def.Key, Kind = def.Kind, Title = def.Title, Entity = def.Entity, Status = DefinitionStatus.Published, Version = 1, DraftJson = json,
            CreatedAt = now, UpdatedAt = now, HasDraftChanges = false,
        }, ct);
        await s.AddVersionAsync(new DefinitionVersionRecord { DefinitionId = def.Id, Version = 1, Key = def.Key, Json = json, Hash = Hash(json), PublishedAt = now }, ct);
        await s.ReplaceTriggersAsync(def.Id, TriggerRows(def, def.Id, def.Key, 1, DateTimeOffset.MinValue), ct);
        await s.CommitAsync(ct);
    }

    private static async Task PutRecordDirectAsync(InMemoryWorkflowStore store, string entity, string id, Dictionary<string, object?> values, CancellationToken ct, IWorkflowSession? within = null)
    {
        if (within is InMemoryWorkflowStore.Session open)
        {
            open.Put("records", $"{entity}:{id}", new InMemoryWorkflowStore.StoredRecord { Entity = entity, Id = id, Order = 1, Json = Values.Serialize(values) });
            return;
        }
        await using var s = (InMemoryWorkflowStore.Session)await store.BeginAsync(ct);
        s.Put("records", $"{entity}:{id}", new InMemoryWorkflowStore.StoredRecord { Entity = entity, Id = id, Order = 1, Json = Values.Serialize(values) });
        await s.CommitAsync(ct);
    }
}
