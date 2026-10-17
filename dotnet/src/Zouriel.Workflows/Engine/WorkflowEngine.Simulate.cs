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
    public string? Outcome { get; set; }
    public string? Comment { get; set; }
    public Dictionary<string, object?>? Data { get; set; }
    /// <summary>For <c>decide</c>: who decides (defaults to the task's first undecided assignee).</summary>
    public string? User { get; set; }
    /// <summary>For <c>advance</c>: an ISO duration.</summary>
    public string? Duration { get; set; }
    public string? Signal { get; set; }
    public Dictionary<string, object?>? Values { get; set; }
}

public sealed class SimulationRequest
{
    public string? EntityId { get; set; }
    /// <summary>Record values to use instead of (or on top of) the real record's.</summary>
    public Dictionary<string, object?>? Values { get; set; }
    /// <summary>Automations: the change that starts it (<c>field</c>, <c>from</c>, <c>to</c>), or signal/manual data.</summary>
    public Dictionary<string, object?>? Trigger { get; set; }
    /// <summary>Lifecycles: the state the record is in, and the transition to try.</summary>
    public string? FromState { get; set; }
    public string? Transition { get; set; }
    public Dictionary<string, object?>? Input { get; set; }
    public List<SimulationEvent>? Events { get; set; }
}

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
        var clock = new ManualTimeProvider(Now);
        var log = new SimulationLog(clock.GetUtcNow);
        var store = new InMemoryWorkflowStore { Registry = Registry };
        await using var real = await Options.Store.BeginAsync(ct);
        store.Fallback = real.Entities;
        var sim = new WorkflowEngine(new WorkflowEngineOptions
        {
            Registry = Registry, Store = store, Identity = Options.Identity, Time = clock, Tenant = Options.Tenant, SimulationLog = log,
            FormValidator = Options.FormValidator, MaxStepsPerRun = Options.MaxStepsPerRun, NotifyTaskAssignees = Options.NotifyTaskAssignees,
            TaskNotificationChannel = Options.TaskNotificationChannel, RecordLinkTemplate = Options.RecordLinkTemplate,
        });
        store.Changes = sim.Changes;
        await SeedSimulationAsync(real, store, def, definitionJson, ct);

        string? error = null;
        try
        {
            if (def.Entity is not null && req.EntityId is not null && req.Values is { Count: > 0 })
            {
                var snap = await real.Entities.GetAsync(def.Entity, req.EntityId, ct);
                var values = new Dictionary<string, object?>(snap?.Values ?? [], StringComparer.Ordinal);
                foreach (var (k, v) in req.Values) values[k] = Values.Normalize(v);
                await PutRecordDirectAsync(store, def.Entity, req.EntityId, values, ct);
            }
            await sim.RunAsync(async u =>
            {
                var c = await sim.CompiledAsync(u, def.Id, 1, ct);
                if (def.IsLifecycle)
                {
                    if (req.EntityId is null) throw new WorkflowException("noRecord", "Pick a record to test the lifecycle with.");
                    var lc = c.Lifecycle;
                    var from = req.FromState is null ? null : c.StateByKey(req.FromState);
                    if (from is not null)
                    {
                        var snap = await u.S.Entities.GetAsync(def.Entity!, req.EntityId, ct);
                        var values = new Dictionary<string, object?>(snap?.Values ?? [], StringComparer.Ordinal) { [lc.Field] = from.Key };
                        await PutRecordDirectAsync(store, def.Entity!, req.EntityId, values, ct, u.S);
                    }
                    await sim.EnsureRecordInstanceAsync(u, c, def.Entity!, req.EntityId, ct, actor);
                    if (req.Transition is not null)
                        await sim.ApplyTransitionCoreAsync(u, def.Key, def.Entity!, req.EntityId, req.Transition, req.Input ?? [], actor, isPerson: false, auto: false, 0, ct);
                }
                else
                {
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
            }, ct);
            await DrainAsync(sim, ct);
            foreach (var ev in req.Events ?? [])
            {
                await ApplySimulationEventAsync(sim, store, clock, def, req, ev, ct);
                await DrainAsync(sim, ct);
            }
        }
        catch (WorkflowException e)
        {
            error = e.Message;
            log.Add("error", e.Code, null, e.Message);
        }

        var instances = new List<InstanceView>();
        var tasks = new List<TaskView>();
        await sim.RunAsync(async u =>
        {
            foreach (var i in (await u.S.QueryInstancesAsync(new InstanceQuery { Take = 1000 }, ct)).OrderBy(i => i.StartedAt))
                instances.Add(await sim.InstanceViewAsync(u, i.Id, ct));
            foreach (var t in await u.S.QueryTasksAsync(new TaskQuery { Statuses = [Storage.TaskStatus.Open], Take = 1000 }, ct))
                tasks.Add(await sim.TaskViewAsync(u, t, null, ct));
        }, ct);
        // What a test run shows is masked like the audit log, and fields not readable by workflows are left out.
        Dictionary<string, object?>? record = null;
        await using var view = await store.BeginAsync(ct);
        var mask = await (await sim.OpenAsync(view, ct)).MaskerAsync(def.Entity, req.EntityId, ct);
        if (def.Entity is not null && req.EntityId is not null && (await view.Entities.GetAsync(def.Entity, req.EntityId, ct))?.Values is { } current)
        {
            var readable = current.Where(kv => Registry.Field(def.Entity, kv.Key)?.IsReadable != false).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            record = mask.MaskMap(readable);
        }
        var entries = log.Entries.Select(e => e with { Text = mask.MaskText(e.Text), Data = mask.Mask(e.Data) }).ToList();
        return new SimulationResult(problems, entries, instances, tasks, record, error);
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
        var rows = new List<TriggerRecord>();
        if (def.IsLifecycle && def.Lifecycle is { } lc)
        {
            rows.Add(new TriggerRecord
            {
                Id = NewId(), DefinitionId = def.Id, DefinitionKey = def.Key, Version = 1, Type = "lifecycle", Entity = def.Entity, Field = lc.Field,
                Data = Values.Serialize(new Dictionary<string, object?> { ["mode"] = lc.Mode ?? "enforce", ["initial"] = lc.States.FirstOrDefault(x => x.IsInitial)?.Key }),
            });
        }
        else if (def.Trigger is { } t)
        {
            rows.Add(new TriggerRecord { Id = NewId(), DefinitionId = def.Id, DefinitionKey = def.Key, Version = 1, Type = t.Type, Entity = def.Entity, Field = t.Field, Lifecycle = t.Lifecycle, Signal = t.Signal });
        }
        await s.ReplaceTriggersAsync(def.Id, rows, ct);
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
