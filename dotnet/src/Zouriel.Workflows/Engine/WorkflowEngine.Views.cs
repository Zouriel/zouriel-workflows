using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using TaskStatus = Zouriel.Workflows.Storage.TaskStatus;

namespace Zouriel.Workflows.Engine;

public sealed record StepView(string Id, string NodeId, string Type, string? Title, int Attempt, string Status, object? Input, object? Output, string? Error, string StartedAt, string? EndedAt);

public sealed record InstanceView(
    string Id, string DefinitionId, string DefinitionKey, string Title, int Version, string Kind, string? ParentId, string? Reason, string? Entity, string? EntityId,
    string? EntityTitle, string Status, string? State, string? StateLabel, string? Error, string? ErrorStepId, string? InitiatorId, string? InitiatorName,
    string StartedAt, string UpdatedAt, string? EndedAt, object? Trigger, IReadOnlyList<StepView>? Steps, IReadOnlyList<TokenView>? Tokens,
    object? Outputs, object? Variables, IReadOnlyList<TaskView>? Tasks);

public sealed record TokenView(string Id, string Status, string? Waiting, string? StepId);

public sealed record TaskView(
    string Id, string InstanceId, string Kind, string Title, string? Instructions, string DefinitionKey, string? Entity, string? EntityId, string? EntityTitle,
    string Status, IReadOnlyList<string> Assignees, IReadOnlyList<string> AssigneeNames, object? Rule, object Outcomes, IReadOnlyList<TaskDecision> Decisions,
    JsonNode? Form, string? RequireComment, bool AllowDelegation, string? Outcome, string DueAt, bool Overdue, int EscalationLevel, string CreatedAt,
    string? CompletedAt, bool CanDecide);

public sealed record TransitionOption(string Key, string Label, string To, string ToLabel, string? Tone, string? Confirm, JsonNode? Form);

public sealed record RecordView(
    string Entity, string Id, string? Title, string? Lifecycle, string? LifecycleTitle, string? State, string? StateLabel, string? StateTone, string? Status,
    string? StateEnteredAt, IReadOnlyList<TransitionOption> Transitions, IReadOnlyList<TaskView> Tasks, IReadOnlyList<InstanceView> Instances,
    IReadOnlyList<ManualOption> Manual, IReadOnlyList<object?> States);

public sealed record ManualOption(string Key, string Label, JsonNode? Form);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

public sealed partial class WorkflowEngine
{
    // ── Instances ─────────────────────────────────────────────────────────

    public Task<InstanceView> GetInstanceAsync(string id, CancellationToken ct = default) => RunAsync(u => InstanceViewAsync(u, id, ct), ct);

    public Task<Page<InstanceView>> QueryInstancesAsync(InstanceQuery q, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var items = new List<InstanceView>();
            foreach (var i in await u.S.QueryInstancesAsync(q, ct)) items.Add(await InstanceSummaryAsync(u, i, ct));
            return new Page<InstanceView>(items, await u.S.CountInstancesAsync(q, ct));
        }, ct);

    private async Task<InstanceView> InstanceSummaryAsync(Uow u, InstanceRecord i, CancellationToken ct, bool full = false)
    {
        var c = await CompiledAsync(u, i.DefinitionId, i.Version, ct);
        string? entityTitle = null;
        if (i.Entity is not null && i.EntityId is not null) entityTitle = await RecordTitleAsync(u.Scope(i), i.Entity, i.EntityId, ct);
        var state = c.StateById(i.State);
        // Everything that leaves the engine is masked (spec §11): step data, outputs, variables and the trigger.
        var mask = await u.MaskerAsync(i.Entity, i.EntityId, ct);
        IReadOnlyList<StepView>? steps = null;
        IReadOnlyList<TokenView>? tokens = null;
        IReadOnlyList<TaskView>? tasks = null;
        object? outputs = null, vars = null;
        if (full)
        {
            steps = (await u.S.ListStepsAsync(i.Id, ct)).OrderBy(s => s.StartedAt).Select(s => new StepView(
                s.Id, s.NodeId, s.StepType, c.Step(s.NodeId)?.Title, s.Attempt, s.Status, mask.Mask(s.Input is null ? null : Values.FromJson(s.Input)), mask.Mask(s.Output is null ? null : Values.FromJson(s.Output)),
                mask.MaskText(s.Error), Values.FormatInstant(s.StartedAt), s.EndedAt is null ? null : Values.FormatInstant(s.EndedAt.Value))).ToList();
            var st = RunState.FromJson(i.Data);
            tokens = st.Tokens.Select(t => new TokenView(t.Id, t.Status, t.Waiting, t.Frames.Count == 0 ? null : CurrentStepId(c, t))).ToList();
            outputs = mask.Mask(st.Outputs);
            vars = mask.Mask(st.Vars);
            var list = new List<TaskView>();
            foreach (var t in await u.S.QueryTasksAsync(new TaskQuery { InstanceId = i.Id, Take = 500 }, ct)) list.Add(await TaskViewAsync(u, t, null, ct));
            tasks = list;
        }
        return new InstanceView(i.Id, i.DefinitionId, i.DefinitionKey, c.Definition.Title, i.Version, i.Kind, i.ParentId, i.Reason, i.Entity, i.EntityId, entityTitle,
            i.Status, state?.Key, state?.Label, i.Error, i.ErrorStepId, i.InitiatorId, i.InitiatorName, Values.FormatInstant(i.StartedAt), Values.FormatInstant(i.UpdatedAt),
            i.EndedAt is null ? null : Values.FormatInstant(i.EndedAt.Value), i.TriggerJson is null ? null : mask.MaskTrigger(Values.FromJson(i.TriggerJson)), steps, tokens, outputs, vars, tasks);
    }

    private static string? CurrentStepId(CompiledDefinition c, Token t)
    {
        var f = t.Top;
        return c.Lists.TryGetValue(f.List, out var l) && f.Index < l.Count ? l[f.Index].Id : null;
    }

    internal async Task<InstanceView> InstanceViewAsync(Uow u, string id, CancellationToken ct)
    {
        var i = await u.S.GetInstanceAsync(id, ct) ?? throw new WorkflowException("notFound", "That instance does not exist.");
        return await InstanceSummaryAsync(u, i, ct, full: true);
    }

    /// <summary>The definition version an instance runs, for drawing the run over it.</summary>
    public Task<JsonNode> GetInstanceDefinitionAsync(string id, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var i = await u.S.GetInstanceAsync(id, ct) ?? throw new WorkflowException("notFound", "That instance does not exist.");
            return JsonNode.Parse((await u.S.GetVersionAsync(i.DefinitionId, i.Version, ct))!.Json)!;
        }, ct);

    /// <summary>An admin cancels a running or stuck instance.</summary>
    public Task<InstanceView> CancelAsync(string id, string reason, WorkflowActor admin, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var i = await u.S.GetInstanceAsync(id, ct) ?? throw new WorkflowException("notFound", "That instance does not exist.");
            if (InstanceStatus.IsFinished(i.Status)) throw new WorkflowException("finished", "This instance has already finished.");
            await CancelInstanceAsync(u, i, admin, string.IsNullOrWhiteSpace(reason) ? "Cancelled by an admin." : reason, ct);
            return await InstanceViewAsync(u, id, ct);
        }, ct);

    /// <summary>Runs the failed step of an instance in incident again (the first incident, when there are several).</summary>
    public Task<InstanceView> RetryAsync(string id, WorkflowActor admin, CancellationToken ct = default) => RetryAsync(id, admin, null, ct);

    /// <summary>
    /// Runs one failed step again. <paramref name="target"/> picks the incident by token id or failed step id
    /// (null = the first). Other incidents stay; the instance remains <c>incident</c> until none is left.
    /// </summary>
    public Task<InstanceView> RetryAsync(string id, WorkflowActor admin, string? target, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var (x, t) = await IncidentAsync(u, id, target, ct);
            await u.AuditAsync(IncidentAudit(x, "retried", admin, new Dictionary<string, object?> { ["step"] = t.IncidentStepId, ["token"] = t.Id }), ct);
            t.Top.Attempt = 1;
            await ResumeIncidentAsync(x, t, advance: false, ct);
            return await InstanceViewAsync(u, id, ct);
        }, ct);

    /// <summary>Continues an instance in incident after its failed step (the first incident, when there are several).</summary>
    public Task<InstanceView> SkipAsync(string id, Dictionary<string, object?>? outputs, WorkflowActor admin, CancellationToken ct = default) => SkipAsync(id, outputs, admin, null, ct);

    /// <summary>
    /// Continues after one failed step, optionally giving that step's outputs. <paramref name="target"/> picks
    /// the incident by token id or failed step id (null = the first); other incidents stay.
    /// </summary>
    public Task<InstanceView> SkipAsync(string id, Dictionary<string, object?>? outputs, WorkflowActor admin, string? target, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var (x, t) = await IncidentAsync(u, id, target, ct);
            var stepId = t.IncidentStepId;
            foreach (var (k, v) in outputs ?? []) if (stepId is not null) x.S.SetOutput(stepId, k, v);
            await u.AuditAsync(IncidentAudit(x, "skipped", admin, new Dictionary<string, object?> { ["step"] = stepId, ["token"] = t.Id, ["outputs"] = outputs }), ct);
            await ResumeIncidentAsync(x, t, advance: stepId is not null, ct);
            return await InstanceViewAsync(u, id, ct);
        }, ct);

    private async Task<(Exec X, Token T)> IncidentAsync(Uow u, string id, string? target, CancellationToken ct)
    {
        var x = await LoadExecAsync(u, id, ct);
        if (x.I.Status != InstanceStatus.Incident) throw new WorkflowException("notInIncident", "Only a stopped instance can be retried or skipped.");
        var t = x.S.Tokens.FirstOrDefault(t => t.Waiting == "incident" && (target is null || t.Id == target || t.IncidentStepId == target))
                ?? throw new WorkflowException("notInIncident", target is null ? "Nothing is waiting to be retried." : $"No incident matches '{target}'.");
        return (x, t);
    }

    /// <summary>Resumes one incident token; the instance shows the next incident, if any is left, while the token runs on.</summary>
    private async Task ResumeIncidentAsync(Exec x, Token t, bool advance, CancellationToken ct)
    {
        Resume(t);
        t.IncidentError = null;
        t.IncidentStepId = null;
        if (advance) Advance(t);
        var next = x.S.Tokens.FirstOrDefault(o => o.Waiting == "incident");
        x.I.Status = next is null ? InstanceStatus.Running : InstanceStatus.Incident;
        x.I.Error = next?.IncidentError;
        x.I.ErrorStepId = next?.IncidentStepId;
        await DriveAsync(x, ct);
    }

    private static AuditEntry IncidentAudit(Exec x, string action, WorkflowActor admin, Dictionary<string, object?> data) => new()
    {
        Category = "instance", Action = action, SubjectType = "instance", SubjectId = x.I.Id, InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId,
        Entity = x.I.Entity, EntityId = x.I.EntityId, Actor = admin, CorrelationId = x.I.CorrelationId, Data = data,
    };

    /// <summary>
    /// Moves a lifecycle record (or a waiting automation) to another version. Record states map by key
    /// unless <paramref name="stateMap"/> says otherwise; an automation's waiting steps must exist in the new version.
    /// </summary>
    public Task<InstanceView> MigrateAsync(string id, int toVersion, Dictionary<string, string>? stateMap, WorkflowActor admin, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var i = await u.S.GetInstanceAsync(id, ct) ?? throw new WorkflowException("notFound", "That instance does not exist.");
            if (InstanceStatus.IsFinished(i.Status)) throw new WorkflowException("finished", "A finished instance cannot move to another version.");
            var from = await CompiledAsync(u, i.DefinitionId, i.Version, ct);
            var to = await CompiledAsync(u, i.DefinitionId, toVersion, ct);
            if (i.Kind == InstanceKinds.Record) await MigrateRecordAsync(u, i, from, to, stateMap, admin, ct);
            else EnsureSameSteps(RunState.FromJson(i.Data), from, to);
            i.Version = toVersion;
            i.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(i, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "instance", Action = "migrated", SubjectType = "instance", SubjectId = id, InstanceId = id, DefinitionId = i.DefinitionId, Entity = i.Entity,
                EntityId = i.EntityId, Actor = admin, CorrelationId = i.CorrelationId,
                Before = new Dictionary<string, object?> { ["version"] = (double)from.Version }, After = new Dictionary<string, object?> { ["version"] = (double)toVersion },
                Data = new Dictionary<string, object?> { ["stateMap"] = stateMap },
            }, ct);
            return await InstanceViewAsync(u, id, ct);
        }, ct);

    /// <summary>Moves a record instance's state to the new version by key (or <paramref name="stateMap"/>), replanning its timers.</summary>
    private async Task MigrateRecordAsync(Uow u, InstanceRecord i, CompiledDefinition from, CompiledDefinition to, Dictionary<string, string>? stateMap, WorkflowActor admin, CancellationToken ct)
    {
        var current = from.StateById(i.State);
        var key = current is null ? null : stateMap?.GetValueOrDefault(current.Key) ?? current.Key;
        var target = key is null ? null : to.StateByKey(key);
        if (current is not null && target is null) throw new WorkflowException("unmappedState", $"Version {to.Version} has no state for “{current.Label}”. Choose where it should go.");
        if (target is not null && target.Key != current?.Key) await WriteStateAsync(u, to, i.Entity!, i.EntityId!, target, admin, 0, i.Id, ct);
        foreach (var b in await u.S.ListBookmarksAsync(i.Id, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        i.State = target?.Id;
        if (target is null) return;
        i.Status = target.IsTerminal ? InstanceStatus.Completed : i.Status == InstanceStatus.OutOfSync ? InstanceStatus.Active : i.Status;
        i.EndedAt = target.IsTerminal ? u.Now : null;
        if (!target.IsTerminal) await PlanStateTimersAsync(u, to, i, target, ct);
    }

    /// <summary>Every position a live token holds must point at the same step id in the new version.</summary>
    private static void EnsureSameSteps(RunState state, CompiledDefinition from, CompiledDefinition to)
    {
        foreach (var t in state.Tokens.Where(t => t.Status is TokenStatus.Active or TokenStatus.Waiting))
        foreach (var f in t.Frames)
        {
            var old = from.Lists.GetValueOrDefault(f.List);
            var same = to.Lists.TryGetValue(f.List, out var list) && old is not null && (f.Index < old.Count
                ? f.Index < list.Count && list[f.Index].Id == old[f.Index].Id
                : f.Index >= list.Count);
            if (!same) throw new WorkflowException("cannotMigrate", $"Version {to.Version} no longer has the step this run is at. Cancel it or keep it on version {from.Version}.");
        }
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    public Task<Page<TaskView>> QueryTasksAsync(TaskQuery q, WorkflowActor? viewer, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var items = new List<TaskView>();
            foreach (var t in await u.S.QueryTasksAsync(q, ct)) items.Add(await TaskViewAsync(u, t, viewer, ct));
            return new Page<TaskView>(items, await u.S.CountTasksAsync(q, ct));
        }, ct);

    public Task<TaskView> GetTaskAsync(string id, WorkflowActor? viewer, CancellationToken ct = default) =>
        RunAsync(async u => await TaskViewAsync(u, await u.S.GetTaskAsync(id, ct) ?? throw new WorkflowException("notFound", "That task does not exist."), viewer, ct), ct);

    internal async Task<TaskView> TaskViewAsync(Uow u, TaskRecord t, WorkflowActor? viewer, CancellationToken ct)
    {
        var assignees = (Values.FromJson(t.Assignees) as List<object?> ?? []).Select(Values.Text).ToList();
        var names = new List<string>();
        foreach (var a in assignees) names.Add((await Options.Identity.GetUserAsync(a, ct))?.Name ?? a);
        var mask = await u.MaskerAsync(t.Entity, t.EntityId, ct);
        var decisions = ParseDecisions(t).Select(d => d with { Data = d.Data is null ? null : mask.MaskMap(d.Data), Comment = mask.MaskText(d.Comment) }).ToList();
        var canDecide = t.Status == TaskStatus.Open && viewer?.IsUser == true && assignees.Contains(viewer.Id!) && decisions.All(d => d.UserId != viewer.Id);
        return new TaskView(t.Id, t.InstanceId, t.Kind, t.Title, t.Instructions, t.DefinitionKey, t.Entity, t.EntityId, t.EntityTitle, t.Status, assignees, names,
            t.Rule is null ? null : Values.FromJson(t.Rule), Values.FromJson(t.Outcomes) ?? new List<object?>(), decisions, t.Form is null ? null : JsonNode.Parse(t.Form),
            t.RequireComment, t.AllowDelegation, t.Outcome, Values.FormatInstant(t.DueAt), t.Status == TaskStatus.Open && t.DueAt < u.Now, t.EscalationLevel,
            Values.FormatInstant(t.CreatedAt), t.CompletedAt is null ? null : Values.FormatInstant(t.CompletedAt.Value), canDecide);
    }

    // ── Records ───────────────────────────────────────────────────────────

    /// <summary>Everything a record page shows: its state, what this person can do, its tasks and runs.</summary>
    public Task<RecordView> GetRecordAsync(string entity, string id, WorkflowActor viewer, CancellationToken ct = default) =>
        RunAsync(u => RecordViewAsync(u, entity, id, viewer, ct), ct);

    internal async Task<RecordView> RecordViewAsync(Uow u, string entity, string id, WorkflowActor viewer, CancellationToken ct)
    {
        if (Registry.Entity(entity) is null) throw new WorkflowException("unknownEntity", $"'{entity}' is not a workflow record type.");
        var snap = await u.S.Entities.GetAsync(entity, id, ct) ?? throw new WorkflowException("noRecord", "That record does not exist.");
        var title = Registry.Entity(entity)?.TitleField is { } tf ? Values.Text(snap[tf]) : null;
        var c = await LifecycleForAsync(u, entity, null, null, ct);
        InstanceRecord? record = null;
        var options = new List<TransitionOption>();
        var states = new List<object?>();
        CompiledDefinition? rc = null;
        if (c is not null)
        {
            // Reading never writes: a record without a lifecycle instance yet is shown as it would be created.
            record = await FindRecordInstanceAsync(u, c.DefinitionId, entity, id, ct) ?? TransientRecordInstance(c, entity, id, snap, u.Now);
            rc = record.Version == c.Version ? c : await CompiledAsync(u, record.DefinitionId, record.Version, ct);
            var lc = rc.Lifecycle;
            foreach (var s in lc.States)
                states.Add(new Dictionary<string, object?> { ["key"] = s.Key, ["label"] = s.Label, ["category"] = s.Category, ["tone"] = s.Tone });
            if (record.State is not null)
            {
                var scope = u.Scope(record);
                foreach (var t in lc.Transitions)
                {
                    if (t.Hidden == true || !t.From.Allows(lc, record.State, t.To)) continue;
                    if (!await MayApplyAsync(u, scope, t, viewer, record, ct)) continue;
                    if (!await scope.ConditionAsync(t.When, ct)) continue;
                    var to = rc.StateById(t.To)!;
                    options.Add(new TransitionOption(t.Key, t.Label, to.Key, to.Label, t.Tone, t.Confirm, t.Input?.Form.DeepClone()));
                }
            }
        }
        var tasks = new List<TaskView>();
        foreach (var t in await u.S.QueryTasksAsync(new TaskQuery { Entity = entity, EntityId = id, Statuses = [TaskStatus.Open], Take = 100 }, ct))
            tasks.Add(await TaskViewAsync(u, t, viewer, ct));
        var instances = new List<InstanceView>();
        foreach (var i in await u.S.QueryInstancesAsync(new InstanceQuery { Entity = entity, EntityId = id, Kinds = [InstanceKinds.Automation, InstanceKinds.Run], Take = 50 }, ct))
            instances.Add(await InstanceSummaryAsync(u, i, ct));
        var manual = new List<ManualOption>();
        foreach (var trig in (await u.S.ListTriggersAsync(ct)).Where(t => t.Type == Model.TriggerTypes.Manual && t.Entity == entity))
        {
            var mc = await CompiledAsync(u, trig.DefinitionId, trig.Version, ct);
            var mt = mc.Definition.Trigger!;
            if (!await MayStartManualAsync(u, mt, viewer, entity, id, ct)) continue;
            manual.Add(new ManualOption(mc.Definition.Key, mt.Label ?? mc.Definition.Title, mt.Form?.DeepClone()));
        }
        var state = rc?.StateById(record?.State);
        return new RecordView(entity, id, title, c?.Definition.Key, c?.Definition.Title, state?.Key, state?.Label, state?.Tone, record?.Status,
            record?.StateEnteredAt is null ? null : Values.FormatInstant(record.StateEnteredAt.Value), options, tasks, instances, manual, states);
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AuditRecord>> QueryAuditAsync(AuditQuery q, CancellationToken ct = default) =>
        RunAsync(u => u.S.QueryAuditAsync(q, ct), ct);

    private const int AuditPage = 1000;

    /// <summary>
    /// Checks the hash chain from <paramref name="fromSeq"/> (oldest first), one page at a time: first the
    /// purge checkpoints are collected (they explain gaps), then entries are streamed through the verifier.
    /// </summary>
    public async Task<AuditVerification> VerifyAuditAsync(long fromSeq = 1, CancellationToken ct = default)
    {
        var purged = new List<SeqRange>();
        for (long next = 1; ;)
        {
            var page = await RunAsync(u => u.S.QueryAuditAsync(new AuditQuery { Category = "audit", Action = "purged", FromSeq = next, Take = AuditPage }, ct), ct);
            foreach (var p in page) purged.AddRange(PurgedRanges(p));
            if (page.Count < AuditPage) break;
            next = page[^1].Seq + 1;
        }
        var verifier = new AuditChainVerifier(purged, fromSeq);
        for (var next = fromSeq; ;)
        {
            var page = await RunAsync(u => u.S.QueryAuditAsync(new AuditQuery { FromSeq = next, Take = AuditPage }, ct), ct);
            foreach (var e in page)
                if (!verifier.Add(e)) return verifier.Result();
            if (page.Count < AuditPage) break;
            next = page[^1].Seq + 1;
        }
        // The head is read without the unit's write lock: a writer committing meanwhile can only move it ahead,
        // which shows up as entries missing at the end; read it before the last page in a busy system.
        await using var s = await Options.Store.BeginAsync(ct);
        return verifier.Result(await s.GetAuditHeadAsync(Options.Tenant, ct));
    }

    /// <summary>The exact ranges an <c>audit.purged</c> checkpoint recorded (<c>data.ranges: [[from, to], …]</c>).</summary>
    internal static IEnumerable<SeqRange> PurgedRanges(AuditRecord checkpoint)
    {
        if (checkpoint.Data is null || Values.FromJson(checkpoint.Data) is not Dictionary<string, object?> d) yield break;
        foreach (var r in d.GetValueOrDefault("ranges") as List<object?> ?? [])
            if (r is List<object?> { Count: 2 } pair && pair[0] is double f && pair[1] is double t) yield return new SeqRange((long)f, (long)t);
    }

    /// <summary>Deletes audit entries older than the retention set in settings, leaving a checkpoint per category with the exact ranges deleted.</summary>
    public Task<int> PurgeAuditAsync(WorkflowActor actor, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var total = 0;
            foreach (var (category, days) in u.Settings.Audit?.RetentionDays ?? [])
            {
                if (days < 1 || category == "audit") continue;
                var before = u.Now.AddDays(-days);
                var result = await u.S.PurgeAuditAsync(category, before, ct);
                if (result.Count == 0) continue;
                total += result.Count;
                await u.AuditAsync(new AuditEntry
                {
                    Category = "audit", Action = "purged", SubjectType = "audit", SubjectId = category, Actor = actor,
                    Data = new Dictionary<string, object?>
                    {
                        ["category"] = category, ["before"] = Values.FormatInstant(before), ["count"] = (double)result.Count,
                        ["ranges"] = result.Ranges.Select(r => (object?)new List<object?> { (double)r.From, (double)r.To }).ToList(),
                    },
                }, ct);
            }
            return total;
        }, ct);

    /// <summary>Records that someone exported audit entries (exports are audited too).</summary>
    public Task NoteAuditExportAsync(WorkflowActor actor, string format, int count, object? filter, CancellationToken ct = default) =>
        RunAsync(async u => await u.AuditAsync(new AuditEntry
        {
            Category = "audit", Action = "exported", SubjectType = "audit", SubjectId = "export", Actor = actor,
            Data = new Dictionary<string, object?> { ["format"] = format, ["count"] = (double)count, ["filter"] = Values.Normalize(filter) },
        }, ct), ct);

    // ── Watching overview (global settings) ───────────────────────────────

    public sealed record TriggerOverview(string DefinitionId, string DefinitionKey, string Title, string Kind, string Type, string? Entity, string? Field, string Summary, int Version);

    /// <summary>Every active trigger and lifecycle, for the "Watching" section of global settings.</summary>
    public Task<IReadOnlyList<TriggerOverview>> ListTriggersAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<TriggerOverview>>(async u =>
        {
            var list = new List<TriggerOverview>();
            foreach (var t in await u.S.ListTriggersAsync(ct))
            {
                var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
                list.Add(new TriggerOverview(t.DefinitionId, t.DefinitionKey, c.Definition.Title, c.Definition.Kind, t.Type, t.Entity, t.Field, Describe(c.Definition, t), t.Version));
            }
            return list;
        }, ct);

    private string Describe(WorkflowDefinition d, TriggerRecord t)
    {
        var entity = Registry.Entity(t.Entity);
        var field = entity?.Field(t.Field);
        var tr = d.Trigger;
        string Values(List<JsonNode?>? v) => v is { Count: > 0 } ? string.Join(" or ", v.Select(x => field?.OptionLabel(Json.Values.FromJson(x)) ?? x?.ToString())) : "anything";
        return t.Type switch
        {
            "lifecycle" => $"Controls {entity?.Label ?? t.Entity} {field?.Label ?? t.Field}",
            "created" => $"When a {entity?.Label ?? t.Entity} is created",
            "deleted" => $"When a {entity?.Label ?? t.Entity} is deleted",
            "fieldChanged" => $"When {entity?.Label} {field?.Label ?? t.Field} changes" + (tr?.From is { Count: > 0 } ? $" from {Values(tr.From)}" : "") + (tr?.To is { Count: > 0 } ? $" to {Values(tr.To)}" : ""),
            "stateEntered" => $"When {t.Lifecycle} enters {string.Join(" or ", tr?.States ?? [])}",
            "transitionApplied" => $"After {t.Lifecycle}: {string.Join(" or ", tr?.Transitions ?? [])}",
            "dateReached" => $"{tr?.Offset} from {entity?.Label} {field?.Label ?? t.Field}",
            "schedule" => $"On schedule {tr?.Cron}",
            "manual" => $"Button “{tr?.Label}” on {entity?.Label ?? "nothing"}",
            "signal" => $"When the app sends {t.Signal}",
            _ => t.Type,
        };
    }
}
