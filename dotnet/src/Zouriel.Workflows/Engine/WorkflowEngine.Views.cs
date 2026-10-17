using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;
using TaskStatus = Zouriel.Workflows.Storage.TaskStatus;

namespace Zouriel.Workflows.Engine;

/// <summary>One executed step of an instance, as the run viewer shows it (inputs, outputs and errors are masked, spec §11).</summary>
/// <param name="Id">The step record's id (one per attempt).</param>
/// <param name="NodeId">The step's id in the definition.</param>
/// <param name="Type">The step type (<c>approval</c>, <c>webhook</c>…).</param>
/// <param name="Title">The step's title in the definition version the instance runs, if it has one.</param>
/// <param name="Attempt">The attempt number (1 for the first try, higher after retries).</param>
/// <param name="Status">One of <see cref="Storage.StepStatus"/>: running, waiting, completed, failed, skipped, cancelled.</param>
/// <param name="Input">What the step was given (recipients, URL, action input…).</param>
/// <param name="Output">What the step produced (spec §7 outputs).</param>
/// <param name="Error">Why the attempt failed, if it did.</param>
/// <param name="StartedAt">When the attempt started (ISO 8601 UTC).</param>
/// <param name="EndedAt">When it ended, or null while it runs or waits.</param>
public sealed record StepView(string Id, string NodeId, string Type, string? Title, int Attempt, string Status, object? Input, object? Output, string? Error, string StartedAt, string? EndedAt);

/// <summary>An instance (automation run, lifecycle record instance or child run), as the API and run viewer show it. Data is masked (spec §11).</summary>
/// <param name="Id">The instance id.</param>
/// <param name="DefinitionId">The definition it runs.</param>
/// <param name="DefinitionKey">That definition's key.</param>
/// <param name="Title">The definition's title in the pinned version.</param>
/// <param name="Version">The definition version the instance is pinned to.</param>
/// <param name="Kind">One of <see cref="Storage.InstanceKinds"/>: automation, record or run.</param>
/// <param name="ParentId">For child runs and waited-for sub-workflows: the instance that started it.</param>
/// <param name="Reason">For runs: which list it runs (<c>transition:ID</c>, <c>onEnter:ID</c>…); for sub-workflows, the parent step.</param>
/// <param name="Entity">The record type the instance is about, if any.</param>
/// <param name="EntityId">The record's id, if any.</param>
/// <param name="EntityTitle">The record's title (its registry title field), when it can be read.</param>
/// <param name="Status">One of <see cref="Storage.InstanceStatus"/> (spec §10).</param>
/// <param name="State">For record instances: the current state key.</param>
/// <param name="StateLabel">That state's label.</param>
/// <param name="Error">The error of the incident (or failure) the instance shows.</param>
/// <param name="ErrorStepId">The step that failed, for that incident.</param>
/// <param name="InitiatorId">The person who started it (null for system starts).</param>
/// <param name="InitiatorName">That person's name.</param>
/// <param name="StartedAt">When it started (ISO 8601 UTC).</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="EndedAt">When it finished, or null.</param>
/// <param name="Trigger">What started it (spec §3 <c>trigger.*</c>), masked.</param>
/// <param name="Steps">The executed steps, oldest first (single-instance reads only).</param>
/// <param name="Tokens">The interpreter's threads and what each waits for (single-instance reads only).</param>
/// <param name="Outputs">Step outputs by step id, masked (single-instance reads only).</param>
/// <param name="Variables">The workflow variables, masked (single-instance reads only).</param>
/// <param name="Tasks">The instance's tasks (single-instance reads only).</param>
public sealed record InstanceView(
    string Id, string DefinitionId, string DefinitionKey, string Title, int Version, string Kind, string? ParentId, string? Reason, string? Entity, string? EntityId,
    string? EntityTitle, string Status, string? State, string? StateLabel, string? Error, string? ErrorStepId, string? InitiatorId, string? InitiatorName,
    string StartedAt, string UpdatedAt, string? EndedAt, object? Trigger, IReadOnlyList<StepView>? Steps, IReadOnlyList<TokenView>? Tokens,
    object? Outputs, object? Variables, IReadOnlyList<TaskView>? Tasks);

/// <summary>One thread of an instance: parallel paths and parallel forEach items each run in their own.</summary>
/// <param name="Id">The token id (<c>root</c>, <c>t1</c>…).</param>
/// <param name="Status">active, waiting, done or cancelled.</param>
/// <param name="Waiting">What it waits for: <c>task:ID</c>, <c>timer</c>, <c>signal</c>, <c>condition</c>, <c>join</c>, <c>child:ID</c>, <c>retry</c> or <c>incident</c>.</param>
/// <param name="StepId">The step it is at, if any.</param>
public sealed record TokenView(string Id, string Status, string? Waiting, string? StepId);

/// <summary>An approval or task waiting for (or decided by) people, as the inbox and record panel show it. Decision answers are masked.</summary>
/// <param name="Id">The task id.</param>
/// <param name="InstanceId">The instance whose step created it.</param>
/// <param name="Kind"><c>approval</c> or <c>task</c>.</param>
/// <param name="Title">The step's title (or "Approval"/"Task").</param>
/// <param name="Instructions">The rendered instructions template, if any.</param>
/// <param name="DefinitionKey">The workflow it belongs to.</param>
/// <param name="Entity">The record type it is about, if any.</param>
/// <param name="EntityId">The record id, if any.</param>
/// <param name="EntityTitle">The record's title when the task was created.</param>
/// <param name="Status">open, completed, cancelled or timedOut.</param>
/// <param name="Assignees">User ids who may decide.</param>
/// <param name="AssigneeNames">Their names, in the same order.</param>
/// <param name="Rule">For approvals: <c>{ type, value }</c> (spec §7 assignee rules).</param>
/// <param name="Outcomes">The choices: <c>[{ key, label, tone }]</c>.</param>
/// <param name="Decisions">The decisions made so far.</param>
/// <param name="Form">The form to fill in when deciding, if any.</param>
/// <param name="RequireComment"><c>never</c>, <c>reject</c> (an approval's <c>rejected</c>, or a task outcome whose tone is <c>danger</c>, needs a comment) or <c>always</c>.</param>
/// <param name="AllowDelegation">Whether an assignee may hand it to someone else.</param>
/// <param name="Outcome">The settled outcome, once there is one.</param>
/// <param name="DueAt">When it times out (ISO 8601 UTC).</param>
/// <param name="Overdue">True while open past its due time.</param>
/// <param name="EscalationLevel">How many times it has been escalated.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="CompletedAt">When it was settled, cancelled or timed out.</param>
/// <param name="CanDecide">True when the viewer is an assignee who has not decided yet and the task is open.</param>
public sealed record TaskView(
    string Id, string InstanceId, string Kind, string Title, string? Instructions, string DefinitionKey, string? Entity, string? EntityId, string? EntityTitle,
    string Status, IReadOnlyList<string> Assignees, IReadOnlyList<string> AssigneeNames, object? Rule, object Outcomes, IReadOnlyList<TaskDecision> Decisions,
    JsonNode? Form, string? RequireComment, bool AllowDelegation, string? Outcome, string DueAt, bool Overdue, int EscalationLevel, string CreatedAt,
    string? CompletedAt, bool CanDecide);

/// <summary>A transition the viewer may apply to a record now (its <c>who</c> and guard hold, spec §6).</summary>
/// <param name="Key">The transition key to apply.</param>
/// <param name="Label">Its button label.</param>
/// <param name="To">The target state key.</param>
/// <param name="ToLabel">The target state's label.</param>
/// <param name="Tone">The button tone, if set.</param>
/// <param name="Confirm">A confirmation question to ask first, if set.</param>
/// <param name="Form">The input form to fill in, if the transition asks for one.</param>
/// <param name="Comment">Whether the person is asked for remarks: <c>none</c>, <c>optional</c> or <c>required</c> (resolved, never null).</param>
public sealed record TransitionOption(string Key, string Label, string To, string ToLabel, string? Tone, string? Confirm, JsonNode? Form, string Comment);

/// <summary>Starting the record's workflow over, offered to the people the lifecycle's reset allows (spec §6).</summary>
/// <param name="Label">The button label ("Reset workflow" by default).</param>
/// <param name="To">The state key it goes back to.</param>
/// <param name="ToLabel">That state's label.</param>
/// <param name="Confirm">A confirmation question to ask first, if set.</param>
/// <param name="Form">The form to fill in (e.g. a reason), if the reset asks for one.</param>
/// <param name="Comment">Whether the person is asked for remarks: <c>none</c>, <c>optional</c> or <c>required</c> (resolved, never null).</param>
public sealed record ResetOption(string Label, string To, string ToLabel, string? Confirm, JsonNode? Form, string Comment);

/// <summary>
/// Everything a record page shows (the record panel): its lifecycle state, the transitions the viewer may
/// apply, open tasks, recent runs and manual starts. Reading never creates the lifecycle instance; a
/// record without one is shown in the state it would get.
/// </summary>
/// <param name="Entity">The record type.</param>
/// <param name="Id">The record id.</param>
/// <param name="Title">The record's title (registry title field).</param>
/// <param name="Lifecycle">The key of the lifecycle that owns a field of this entity, if any.</param>
/// <param name="LifecycleTitle">That lifecycle's title.</param>
/// <param name="State">The current state key.</param>
/// <param name="StateLabel">The current state's label.</param>
/// <param name="StateTone">The current state's tone.</param>
/// <param name="Status">The record instance's status (<c>active</c>, <c>completed</c>, <c>outOfSync</c>…).</param>
/// <param name="StateEnteredAt">When the record entered its state, if known.</param>
/// <param name="Transitions">What the viewer may apply now.</param>
/// <param name="Tasks">Open tasks about the record.</param>
/// <param name="Instances">Recent automation runs and lifecycle child runs about the record.</param>
/// <param name="Manual">Manual workflows the viewer may start from the record.</param>
/// <param name="States">All states of the lifecycle: <c>{ key, label, category, tone }</c>.</param>
/// <param name="Reset">Starting the workflow over, when the viewer may (null otherwise).</param>
public sealed record RecordView(
    string Entity, string Id, string? Title, string? Lifecycle, string? LifecycleTitle, string? State, string? StateLabel, string? StateTone, string? Status,
    string? StateEnteredAt, IReadOnlyList<TransitionOption> Transitions, IReadOnlyList<TaskView> Tasks, IReadOnlyList<InstanceView> Instances,
    IReadOnlyList<ManualOption> Manual, IReadOnlyList<object?> States, ResetOption? Reset);

/// <summary>A manual-trigger workflow the viewer may start from a record (spec §5 <c>manual</c>).</summary>
/// <param name="Key">The workflow key to start.</param>
/// <param name="Label">The button label.</param>
/// <param name="Form">The form to fill in first, if any.</param>
public sealed record ManualOption(string Key, string Label, JsonNode? Form);

/// <summary>One page of a list, with the total number of matches.</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Total">How many items match in all.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

public sealed partial class WorkflowEngine
{
    // ── Instances ─────────────────────────────────────────────────────────

    /// <summary>One instance with its steps, tokens, outputs, variables and tasks (masked). Throws <c>notFound</c>.</summary>
    public Task<InstanceView> GetInstanceAsync(string id, CancellationToken ct = default) => RunAsync(u => InstanceViewAsync(u, id, ct), ct);

    /// <summary>A page of instances matching the query (summaries without steps or outputs).</summary>
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
            i.Status, state?.Key, state?.Label, mask.MaskText(i.Error), i.ErrorStepId, i.InitiatorId, i.InitiatorName, Values.FormatInstant(i.StartedAt), Values.FormatInstant(i.UpdatedAt),
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

    /// <summary>A page of tasks matching the query; <paramref name="viewer"/> decides <see cref="TaskView.CanDecide"/>.</summary>
    public Task<Page<TaskView>> QueryTasksAsync(TaskQuery q, WorkflowActor? viewer, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var items = new List<TaskView>();
            foreach (var t in await u.S.QueryTasksAsync(q, ct)) items.Add(await TaskViewAsync(u, t, viewer, ct));
            return new Page<TaskView>(items, await u.S.CountTasksAsync(q, ct));
        }, ct);

    /// <summary>One task as <paramref name="viewer"/> sees it. Throws <c>notFound</c>.</summary>
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
        // Titles and instructions are rendered from the record and may quote its sensitive values (spec §11).
        return new TaskView(t.Id, t.InstanceId, t.Kind, mask.MaskText(t.Title)!, mask.MaskText(t.Instructions), t.DefinitionKey, t.Entity, t.EntityId, mask.MaskText(t.EntityTitle), t.Status, assignees, names,
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
        var c = await LifecycleForRecordAsync(u, entity, id, null, ct, snap.Values);
        InstanceRecord? record = null;
        var options = new List<TransitionOption>();
        var states = new List<object?>();
        ResetOption? reset = null;
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
                    if (!await MayApplyAsync(scope, t, viewer, record, ct)) continue;
                    if (!await scope.ConditionAsync(t.When, ct)) continue;
                    var to = rc.StateById(t.To)!;
                    options.Add(new TransitionOption(t.Key, t.Label, to.Key, to.Label, t.Tone, t.Confirm, t.Input?.Form.DeepClone(), t.ResolvedComment));
                }
            }
            reset = await OfferedResetAsync(u, rc, record, viewer, ct);
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
            record?.StateEnteredAt is null ? null : Values.FormatInstant(record.StateEnteredAt.Value), options, tasks, instances, manual, states, reset);
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    /// <summary>Audit entries matching the query (newest first, or oldest first from <see cref="AuditQuery.FromSeq"/>). Entries are stored masked.</summary>
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

    /// <summary>One active trigger or lifecycle, for the "Watching" section of global settings.</summary>
    /// <param name="DefinitionId">The definition it belongs to.</param>
    /// <param name="DefinitionKey">That definition's key.</param>
    /// <param name="Title">That definition's title.</param>
    /// <param name="Kind"><c>automation</c> or <c>lifecycle</c>.</param>
    /// <param name="Type">The trigger type (spec §5), or <c>lifecycle</c> for an owned field.</param>
    /// <param name="Entity">The record type it watches, if any.</param>
    /// <param name="Field">The field it watches or owns, if any.</param>
    /// <param name="Summary">A plain-English description ("When Application Stage changes to Done").</param>
    /// <param name="Version">The published version the trigger comes from.</param>
    public sealed record TriggerOverview(string DefinitionId, string DefinitionKey, string Title, string Kind, string Type, string? Entity, string? Field, string Summary, int Version);

    /// <summary>Every active trigger and lifecycle, for the "Watching" section of global settings.</summary>
    public Task<IReadOnlyList<TriggerOverview>> ListTriggersAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<TriggerOverview>>(async u =>
        {
            var list = new List<TriggerOverview>();
            foreach (var t in await u.S.ListTriggersAsync(ct))
            {
                var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
                // Lifecycle triggers name the lifecycle's states and transitions, so read its published version.
                CompiledDefinition? lc = null;
                if (t.Lifecycle is { } key && await u.S.GetDefinitionByKeyAsync(key, ct) is { Version: > 0 } rec)
                    lc = await CompiledAsync(u, rec.Id, rec.Version, ct);
                list.Add(new TriggerOverview(t.DefinitionId, t.DefinitionKey, c.Definition.Title, c.Definition.Kind, t.Type, t.Entity, t.Field, Describe(c.Definition, t, lc), t.Version));
            }
            return list;
        }, ct);

    private string Describe(WorkflowDefinition d, TriggerRecord t, CompiledDefinition? lifecycle)
    {
        var entity = Registry.Entity(t.Entity);
        var field = entity?.Field(t.Field);
        var tr = d.Trigger;
        string Values(List<JsonNode?>? v) => v is { Count: > 0 } ? string.Join(" or ", v.Select(x => field?.OptionLabel(Json.Values.FromJson(x)) ?? x?.ToString())) : "anything";
        static string A(string? word) => $"{(word is { Length: > 0 } && "aeiouAEIOU".Contains(word[0]) ? "an" : "a")} {word}";
        string LifecycleName() => lifecycle?.Definition.Title ?? t.Lifecycle ?? "a lifecycle";
        return t.Type switch
        {
            "lifecycle" => $"Controls {entity?.Label ?? t.Entity} {field?.Label ?? t.Field}",
            "created" => $"When {A(entity?.Label ?? t.Entity)} is created",
            "deleted" => $"When {A(entity?.Label ?? t.Entity)} is deleted",
            "fieldChanged" => $"When {entity?.Label} {field?.Label ?? t.Field} changes" + (tr?.From is { Count: > 0 } ? $" from {Values(tr.From)}" : "") + (tr?.To is { Count: > 0 } ? $" to {Values(tr.To)}" : ""),
            "stateEntered" => $"When {LifecycleName()} enters {string.Join(" or ", (tr?.States ?? []).Select(k => lifecycle?.StateByKey(k)?.Label ?? k))}",
            "transitionApplied" => $"After “{string.Join("” or “", (tr?.Transitions ?? []).Select(k => lifecycle?.TransitionByKey(k)?.Label ?? k))}” in {LifecycleName()}",
            "dateReached" => $"{Offset(tr?.Offset)} {entity?.Label} {field?.Label ?? t.Field}",
            "schedule" => $"On schedule {tr?.Cron}",
            "manual" => $"Button “{tr?.Label}” on {entity?.Label ?? "nothing"}",
            "signal" => $"When the app sends {t.Signal}",
            _ => t.Type,
        };
    }

    /// <summary>"2 days before", "3 hours after", "On" for no offset: a date trigger's offset in words.</summary>
    private static string Offset(string? iso)
    {
        if (string.IsNullOrEmpty(iso) || !IsoDuration.TryParse(iso, out var d) || d.IsZero) return "On";
        var parts = new List<string>();
        void Add(int n, string unit) { if (n != 0) parts.Add($"{n} {unit}{(n == 1 ? "" : "s")}"); }
        Add(d.Years, "year"); Add(d.Months, "month"); Add(d.Weeks, "week"); Add(d.Days, "day"); Add(d.Hours, "hour"); Add(d.Minutes, "minute");
        return $"{string.Join(" ", parts)} {(d.Negative ? "before" : "after")}";
    }
}
