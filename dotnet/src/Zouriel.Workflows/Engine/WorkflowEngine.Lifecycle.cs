using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

public sealed partial class WorkflowEngine
{
    /// <summary>The published lifecycle that owns a field of an entity (or any lifecycle of the entity when <paramref name="field"/> is null).</summary>
    internal async Task<CompiledDefinition?> LifecycleForAsync(Uow u, string entity, string? field, string? key, CancellationToken ct)
    {
        foreach (var t in await u.S.ListTriggersAsync(ct))
        {
            if (t.Type != "lifecycle" || t.Entity != entity) continue;
            if (field is not null && t.Field != field) continue;
            if (key is not null && t.DefinitionKey != key) continue;
            return await CompiledAsync(u, t.DefinitionId, t.Version, ct);
        }
        return null;
    }

    internal async Task<CompiledDefinition> RequireLifecycleAsync(Uow u, string key, string entity, CancellationToken ct)
    {
        var c = await LifecycleForAsync(u, entity, null, key, ct);
        return c ?? throw new WorkflowException("unknownLifecycle", $"There is no published lifecycle '{key}' for {entity}.");
    }

    /// <summary>The record's lifecycle instance, created on first use for records that existed before the lifecycle was published.</summary>
    internal async Task<InstanceRecord> EnsureRecordInstanceAsync(Uow u, CompiledDefinition c, string entity, string id, CancellationToken ct, WorkflowActor? actor = null,
        object? initialValue = null)
    {
        var existing = await FindRecordInstanceAsync(u, c.DefinitionId, entity, id, ct);
        if (existing is not null) return existing;
        var snap = await u.S.Entities.GetAsync(entity, id, ct) ?? throw new WorkflowException("noRecord", $"{entity} {id} does not exist.");
        var lc = c.Lifecycle;
        var value = initialValue ?? snap[lc.Field];
        var state = c.StateByKey(value);
        var writeInitial = false;
        if (state is null && Values.IsEmpty(value))
        {
            state = c.InitialState;
            writeInitial = true;
        }
        var inst = new InstanceRecord
        {
            Id = NewId(), DefinitionId = c.DefinitionId, DefinitionKey = c.Definition.Key, Version = c.Version, Kind = InstanceKinds.Record,
            Entity = entity, EntityId = id, Status = state is null ? InstanceStatus.OutOfSync : state.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active,
            State = state?.Id, StateEnteredAt = u.Now, Data = "{}", CorrelationId = NewId(), StartedAt = u.Now, UpdatedAt = u.Now,
            InitiatorId = actor?.IsUser == true ? actor.Id : null, InitiatorName = actor?.IsUser == true ? actor.Name : null,
            EndedAt = state?.IsTerminal == true ? u.Now : null,
        };
        await u.S.AddInstanceAsync(inst, ct);
        u.Records[inst.Id] = inst;
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = state is null ? "outOfSync" : "created", SubjectType = "record", SubjectId = id, Entity = entity, EntityId = id,
            InstanceId = inst.Id, DefinitionId = c.DefinitionId, Actor = actor ?? WorkflowActor.System, CorrelationId = inst.CorrelationId,
            After = state is null ? null : new Dictionary<string, object?> { ["state"] = state.Key },
            Data = new Dictionary<string, object?> { ["version"] = (double)c.Version, ["value"] = value },
        }, ct);
        if (writeInitial) await WriteStateAsync(u, c, entity, id, state!, actor ?? WorkflowActor.System, 0, inst.Id, ct);
        if (state is not null && !state.IsTerminal) await PlanStateTimersAsync(u, c, inst, state, ct);
        return inst;
    }

    /// <summary>The instance a record would get on first use (spec §6), built for display only and never saved.</summary>
    internal static InstanceRecord TransientRecordInstance(CompiledDefinition c, string entity, string id, RecordSnapshot snap, DateTimeOffset now)
    {
        var value = snap[c.Lifecycle.Field];
        var state = c.StateByKey(value) ?? (Values.IsEmpty(value) ? c.InitialState : null);
        return new InstanceRecord
        {
            DefinitionId = c.DefinitionId, DefinitionKey = c.Definition.Key, Version = c.Version, Kind = InstanceKinds.Record, Entity = entity, EntityId = id,
            State = state?.Id, Status = state is null ? InstanceStatus.OutOfSync : state.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active, StartedAt = now, UpdatedAt = now,
        };
    }

    /// <summary>The record's lifecycle instance (not cancelled), as this unit holds it, or null when there is none yet.</summary>
    internal static async Task<InstanceRecord?> FindRecordInstanceAsync(Uow u, string definitionId, string entity, string id, CancellationToken ct)
    {
        var held = u.Records.Values.FirstOrDefault(i => i.DefinitionId == definitionId && i.Entity == entity && i.EntityId == id && i.Status != InstanceStatus.Cancelled);
        if (held is not null) return held;
        var found = (await u.S.QueryInstancesAsync(new InstanceQuery
        {
            DefinitionId = definitionId, Entity = entity, EntityId = id, Kinds = [InstanceKinds.Record], Take = 10,
        }, ct)).Where(i => i.Status != InstanceStatus.Cancelled).OrderByDescending(i => i.StartedAt).FirstOrDefault();
        if (found is null) return null;
        return u.Records.TryGetValue(found.Id, out var same) ? same : u.Records[found.Id] = found;
    }

    private async Task WriteStateAsync(Uow u, CompiledDefinition c, string entity, string id, State state, WorkflowActor actor, int depth, string instanceId, CancellationToken ct)
    {
        // A workflow step applying the transition stays the cause of the field change, so it does not start itself again.
        var outer = WorkflowWriteScope.Current;
        using var scope = WorkflowWriteScope.Begin(actor, depth, outer?.InstanceId ?? instanceId, outer?.DefinitionId ?? c.DefinitionId, outer?.CorrelationId);
        scope.AllowOwned(entity, id, c.Lifecycle.Field);
        await u.S.Entities.SetFieldsAsync(entity, id, new Dictionary<string, object?> { [c.Lifecycle.Field] = state.Key }, ct);
    }

    // ── Applying transitions ──────────────────────────────────────────────

    /// <summary>
    /// A person applies a transition to a record (spec §6): <c>who</c>, the guard and the input are checked,
    /// and hidden transitions are refused. Everything happens in one unit of work. The actor must be a
    /// person (<see cref="WorkflowActor.IsUser"/>); app code that moves records itself calls
    /// <see cref="ApplyTransitionAsSystemAsync"/>.
    /// </summary>
    public Task<RecordView> ApplyTransitionAsync(string entity, string id, string transition, Dictionary<string, object?>? input, WorkflowActor user, string? lifecycle = null, CancellationToken ct = default)
    {
        if (!user.IsUser) throw new WorkflowException("notAllowed", "Only a signed-in person can apply a transition.");
        return RunAsync(async u =>
        {
            var c = await LifecycleOfAsync(u, entity, lifecycle, ct);
            await ApplyTransitionCoreAsync(u, c.Definition.Key, entity, id, transition, input ?? [], user, isPerson: true, auto: false, 0, ct);
            return await RecordViewAsync(u, entity, id, user, ct);
        }, ct);
    }

    /// <summary>
    /// The app applies a transition itself (like an <c>applyTransition</c> step): <c>who</c> is not checked,
    /// the guard and the input are. Use it from trusted code only, never with a request's user.
    /// </summary>
    public Task ApplyTransitionAsSystemAsync(string entity, string id, string transition, Dictionary<string, object?>? input = null, WorkflowActor? actor = null,
        string? lifecycle = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var c = await LifecycleOfAsync(u, entity, lifecycle, ct);
            await ApplyTransitionCoreAsync(u, c.Definition.Key, entity, id, transition, input ?? [], actor ?? WorkflowActor.System, isPerson: false, auto: false,
                WorkflowWriteScope.Current?.Depth ?? 0, ct);
        }, ct);

    private async Task<CompiledDefinition> LifecycleOfAsync(Uow u, string entity, string? lifecycle, CancellationToken ct) =>
        (lifecycle is null ? await LifecycleForAsync(u, entity, null, null, ct) : await RequireLifecycleAsync(u, lifecycle, entity, ct))
        ?? throw new WorkflowException("unknownLifecycle", $"{entity} has no published lifecycle.");

    internal async Task ApplyTransitionCoreAsync(Uow u, string lifecycleKey, string entity, string id, string transitionKey, Dictionary<string, object?> input,
        WorkflowActor actor, bool isPerson, bool auto, int depth, CancellationToken ct, int chain = 0)
    {
        var c = await RequireLifecycleAsync(u, lifecycleKey, entity, ct);
        var record = await EnsureRecordInstanceAsync(u, c, entity, id, ct, actor);
        if (record.Version != c.Version) c = await CompiledAsync(u, record.DefinitionId, record.Version, ct);
        var lc = c.Lifecycle;
        var tr = c.TransitionByKey(transitionKey) ?? throw new WorkflowException("unknownTransition", $"'{transitionKey}' is not a transition of {c.Definition.Title}.");
        if (record.State is null || !tr.From.Allows(lc, record.State, tr.To))
            throw new WorkflowException("notInFromState", $"“{tr.Label}” is not possible from the record's current state.");
        var scope = u.Scope(record, new RunState { Input = input }, null, new TriggerInfo { Type = "transition", Transition = tr.Key });
        if (isPerson && (tr.Hidden == true || !await MayApplyAsync(u, scope, tr, actor, record, ct)))
            throw new WorkflowException("notAllowed", $"You are not allowed to “{tr.Label}”.");
        if (!await scope.ConditionAsync(tr.When, ct))
            throw new WorkflowException("guardFailed", $"“{tr.Label}” is not possible right now.");
        if (tr.Input?.Form is { } form && !auto)
        {
            var errors = Options.FormValidator.Validate(form, input, u.Today);
            if (errors.Count > 0) throw new WorkflowException("invalidInput", "Some answers need attention.", errors);
        }
        var from = c.StateById(record.State)!;
        var to = c.StateById(tr.To)!;
        var trigger = new TriggerInfo
        {
            Type = "transition", Field = lc.Field, From = from.Key, To = to.Key, Transition = tr.Key, ActorType = actor.Type, ActorId = actor.Id,
            ActorName = actor.Name, At = Values.FormatInstant(u.Now),
        };
        await ExitStateAsync(u, c, record, from, actor, trigger, ct);
        await WriteStateAsync(u, c, entity, id, to, actor, depth, record.Id, ct);
        record.State = to.Id;
        record.StateEnteredAt = u.Now;
        record.Status = to.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active;
        record.EndedAt = to.IsTerminal ? u.Now : null;
        record.UpdatedAt = u.Now;
        await u.S.UpdateInstanceAsync(record, ct);
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = auto ? "autoTransitioned" : "transitioned", SubjectType = "record", SubjectId = id, Entity = entity, EntityId = id,
            InstanceId = record.Id, DefinitionId = c.DefinitionId, Actor = actor, CorrelationId = record.CorrelationId,
            Before = new Dictionary<string, object?> { ["state"] = from.Key }, After = new Dictionary<string, object?> { ["state"] = to.Key },
            Data = new Dictionary<string, object?> { ["transition"] = tr.Key, ["label"] = tr.Label, ["input"] = input },
        }, ct);
        u.Log?.Add("lifecycle", "transitioned", id, $"{from.Label} → {to.Label} ({tr.Label})");
        await QueueLifecycleEventAsync(u, c, entity, id, from.Key, to.Key, tr.Key, actor, depth, record.CorrelationId, ct);
        await StartRunAsync(u, record, c, $"transition:{tr.Id}", $"transition:{tr.Id}", trigger, actor, input, ct);
        // The transition's own steps may have moved the record on already; then that state was entered instead.
        if (record.State == to.Id) await EnterStateAsync(u, c, record, to, actor, trigger, depth, chain, ct, input);
    }

    internal Task<bool> MayApplyAsync(Uow u, ValueScope scope, Transition tr, WorkflowActor actor, InstanceRecord record, CancellationToken ct) =>
        IsAmongAsync(scope, tr.Who, actor, ct);

    /// <summary>Whether a signed-in person is one of <paramref name="who"/> (empty = nobody).</summary>
    private async Task<bool> IsAmongAsync(ValueScope scope, List<Assignee>? who, WorkflowActor actor, CancellationToken ct)
    {
        if (!actor.IsUser || who is not { Count: > 0 }) return false;
        var allowed = await ResolveAssigneesAsync(scope, who, null, actor.Id, ct);
        if (allowed.Contains(actor.Id!)) return true;
        // Roles the caller carries (from their login) count too, so apps need not list every member.
        return actor.Roles is { Count: > 0 } roles && who.Any(w => w.Role is not null && roles.Contains(w.Role));
    }

    private async Task ExitStateAsync(Uow u, CompiledDefinition c, InstanceRecord record, State from, WorkflowActor actor, TriggerInfo trigger, CancellationToken ct)
    {
        foreach (var b in await u.S.ListBookmarksAsync(record.Id, ct))
            if (b.Purpose.StartsWith(BookmarkPurposes.AutoPrefix, StringComparison.Ordinal) || b.Purpose.StartsWith(BookmarkPurposes.SlaPrefix, StringComparison.Ordinal)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        if (from.KeepRunsOnExit != true) await CancelStateRunsAsync(u, record, from, ct);
        await StartRunAsync(u, record, c, $"onExit:{from.Id}", $"onExit:{from.Id}", trigger, actor, null, ct);
    }

    /// <summary>
    /// Cancels the onEnter and SLA runs a state started (the record left it). A run that is executing right now
    /// is the one moving the record, so it is left to finish its own steps.
    /// </summary>
    private async Task CancelStateRunsAsync(Uow u, InstanceRecord record, State state, CancellationToken ct)
    {
        foreach (var run in await u.S.QueryInstancesAsync(new InstanceQuery { ParentId = record.Id, Kinds = [InstanceKinds.Run], Take = 1000 }, ct))
        {
            if (InstanceStatus.IsFinished(run.Status) || u.Driving.Contains(run.Id)) continue;
            if (run.Reason == $"onEnter:{state.Id}" || run.Reason == $"sla:{state.Id}")
                await CancelInstanceAsync(u, run, WorkflowActor.System, $"The record left “{state.Label}”.", ct);
        }
    }

    private async Task EnterStateAsync(Uow u, CompiledDefinition c, InstanceRecord record, State to, WorkflowActor actor, TriggerInfo trigger, int depth, int chain,
        CancellationToken ct, Dictionary<string, object?>? input = null)
    {
        // onEnter steps can read the answers of the transition that entered the state (input.*).
        await StartRunAsync(u, record, c, $"onEnter:{to.Id}", $"onEnter:{to.Id}", trigger, actor, input, ct);
        // The onEnter steps may already have moved the record on; then the new state planned its own timers.
        if (to.IsTerminal || record.State != to.Id) return;
        await PlanStateTimersAsync(u, c, record, to, ct);
        await CheckAutoWhenAsync(u, c, record, depth, chain, ct);
    }

    private async Task PlanStateTimersAsync(Uow u, CompiledDefinition c, InstanceRecord record, State state, CancellationToken ct)
    {
        var lc = c.Lifecycle;
        foreach (var t in lc.Transitions)
        {
            if (t.Auto?.After is not { } after || !t.From.Allows(lc, state.Id, t.To) || !IsoDuration.TryParse(after, out var d)) continue;
            var working = (t.Auto.Calendar ?? c.Definition.Settings?.Calendar) == "working";
            await u.S.AddBookmarkAsync(new BookmarkRecord
            {
                Id = NewId(), InstanceId = record.Id, Kind = BookmarkKinds.Timer, Purpose = BookmarkPurposes.AutoPrefix + t.Id, DueAt = u.Calendar.Add(u.Now, d, working),
                Entity = record.Entity, EntityId = record.EntityId, DefinitionId = record.DefinitionId, CreatedAt = u.Now,
            }, ct);
        }
        if (state.Sla is { } sla && IsoDuration.TryParse(sla.Within, out var within))
        {
            var working = (sla.Calendar ?? c.Definition.Settings?.Calendar) == "working";
            await u.S.AddBookmarkAsync(new BookmarkRecord
            {
                Id = NewId(), InstanceId = record.Id, Kind = BookmarkKinds.Timer, Purpose = BookmarkPurposes.SlaPrefix + state.Id, DueAt = u.Calendar.Add(u.Now, within, working),
                Entity = record.Entity, EntityId = record.EntityId, DefinitionId = record.DefinitionId, CreatedAt = u.Now,
            }, ct);
        }
    }

    /// <summary>Applies the first automatic transition whose condition holds in the current state.</summary>
    internal async Task CheckAutoWhenAsync(Uow u, CompiledDefinition c, InstanceRecord record, int depth, int chain, CancellationToken ct)
    {
        if (record.State is null || chain >= 20) return;
        var lc = c.Lifecycle;
        foreach (var t in lc.Transitions)
        {
            if (t.Auto?.When is null || !t.From.Allows(lc, record.State, t.To)) continue;
            var scope = u.Scope(record);
            if (!await scope.ConditionAsync(t.Auto.When, ct) || !await scope.ConditionAsync(t.When, ct)) continue;
            await ApplyTransitionCoreAsync(u, c.Definition.Key, record.Entity!, record.EntityId!, t.Key, [], WorkflowActor.System, isPerson: false, auto: true, depth, ct, chain + 1);
            return;
        }
    }

    private async Task QueueLifecycleEventAsync(Uow u, CompiledDefinition c, string entity, string id, string? from, string to, string? transition, WorkflowActor actor, int depth, string correlation, CancellationToken ct)
    {
        if (u.Simulation) return;
        var ev = new LifecycleEvent
        {
            Lifecycle = c.Definition.Key, Entity = entity, EntityId = id, FromState = from, ToState = to, Transition = transition,
            ActorType = actor.Type, ActorId = actor.Id, ActorName = actor.Name, Depth = depth, CorrelationId = correlation, At = u.Now,
            CausationDefinitionId = WorkflowWriteScope.Current?.DefinitionId,
        };
        await u.S.AddOutboxAsync(new OutboxRecord
        {
            Id = NewId(), Kind = OutboxKinds.Lifecycle, Payload = System.Text.Json.JsonSerializer.Serialize(ev, Definitions.JsonOptions), CreatedAt = u.Now,
        }, ct);
    }

    /// <summary>Puts an out-of-sync record instance back in sync at a state (no steps run; its timers are planned).</summary>
    private async Task FollowStateAsync(Uow u, CompiledDefinition c, InstanceRecord record, State state, CancellationToken ct)
    {
        if (record.State != state.Id) record.StateEnteredAt = u.Now;
        record.State = state.Id;
        record.Status = state.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active;
        record.EndedAt = state.IsTerminal ? u.Now : null;
        record.UpdatedAt = u.Now;
        await u.S.UpdateInstanceAsync(record, ct);
        if (!state.IsTerminal) await PlanStateTimersAsync(u, c, record, state, ct);
    }

    /// <summary>The record's value matches no state a transition leads to: stop the state's runs and timers and wait for a valid value.</summary>
    private async Task MarkOutOfSyncAsync(Uow u, CompiledDefinition c, InstanceRecord record, State? state, object? value, CancellationToken ct)
    {
        foreach (var b in await u.S.ListBookmarksAsync(record.Id, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        if (state is not null && state.KeepRunsOnExit != true) await CancelStateRunsAsync(u, record, state, ct);
        record.Status = InstanceStatus.OutOfSync;
        record.UpdatedAt = u.Now;
        await u.S.UpdateInstanceAsync(record, ct);
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "outOfSync", SubjectType = "record", SubjectId = record.EntityId!, Entity = record.Entity, EntityId = record.EntityId,
            InstanceId = record.Id, DefinitionId = c.DefinitionId, CorrelationId = record.CorrelationId, Data = new Dictionary<string, object?> { ["value"] = value },
        }, ct);
    }

    /// <summary>An admin moves a record to any state, skipping checks (audited with the reason).</summary>
    public Task<RecordView> ForceStateAsync(string entity, string id, string stateKey, string reason, bool skipSteps, WorkflowActor admin, string? lifecycle = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("reasonRequired", "Say why the state is being changed.");
            var c = lifecycle is null ? await LifecycleForAsync(u, entity, null, null, ct) : await RequireLifecycleAsync(u, lifecycle, entity, ct);
            if (c is null) throw new WorkflowException("unknownLifecycle", $"{entity} has no published lifecycle.");
            var record = await EnsureRecordInstanceAsync(u, c, entity, id, ct, admin);
            if (record.Version != c.Version) c = await CompiledAsync(u, record.DefinitionId, record.Version, ct);
            var to = c.StateByKey(stateKey) ?? throw new WorkflowException("unknownState", $"'{stateKey}' is not a state of {c.Definition.Title}.");
            var from = c.StateById(record.State);
            var trigger = new TriggerInfo
            {
                Type = "forced", Field = c.Lifecycle.Field, From = from?.Key, To = to.Key, ActorType = admin.Type, ActorId = admin.Id, ActorName = admin.Name,
                At = Values.FormatInstant(u.Now),
            };
            if (from is not null && !skipSteps) await ExitStateAsync(u, c, record, from, admin, trigger, ct);
            else foreach (var b in await u.S.ListBookmarksAsync(record.Id, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
            await WriteStateAsync(u, c, entity, id, to, admin, 0, record.Id, ct);
            record.State = to.Id;
            record.StateEnteredAt = u.Now;
            record.Status = to.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active;
            record.EndedAt = to.IsTerminal ? u.Now : null;
            record.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(record, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "lifecycle", Action = "forced", SubjectType = "record", SubjectId = id, Entity = entity, EntityId = id, InstanceId = record.Id,
                DefinitionId = c.DefinitionId, Actor = admin, CorrelationId = record.CorrelationId,
                Before = new Dictionary<string, object?> { ["state"] = from?.Key }, After = new Dictionary<string, object?> { ["state"] = to.Key },
                Data = new Dictionary<string, object?> { ["reason"] = reason, ["skipSteps"] = skipSteps },
            }, ct);
            await QueueLifecycleEventAsync(u, c, entity, id, from?.Key, to.Key, null, admin, 0, record.CorrelationId, ct);
            if (!skipSteps) await EnterStateAsync(u, c, record, to, admin, trigger, 0, 0, ct);
            else if (!to.IsTerminal) await PlanStateTimersAsync(u, c, record, to, ct);
            return await RecordViewAsync(u, entity, id, admin, ct);
        }, ct);

    // ── Reset ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A person starts a record's workflow over (spec §6 reset): one of <c>reset.who</c> moves it back to
    /// <c>reset.to</c> (the initial state by default). The record's open runs and tasks are cancelled (and the
    /// automations about it, unless <c>cancelAutomations</c> is false), the state is written, then the reset
    /// steps run (they can change this record and related ones). Everything happens in one unit of work.
    /// </summary>
    public Task<RecordView> ResetAsync(string entity, string id, Dictionary<string, object?>? input, WorkflowActor user, string? lifecycle = null, CancellationToken ct = default)
    {
        if (!user.IsUser) throw new WorkflowException("notAllowed", "Only a signed-in person can reset a workflow.");
        return RunAsync(async u =>
        {
            var c = await LifecycleOfAsync(u, entity, lifecycle, ct);
            var record = await EnsureRecordInstanceAsync(u, c, entity, id, ct, user);
            if (record.Version != c.Version) c = await CompiledAsync(u, record.DefinitionId, record.Version, ct);
            var reset = c.Lifecycle.Reset ?? throw new WorkflowException("resetDisabled", $"{c.Definition.Title} cannot be reset.");
            var label = reset.Label ?? "Reset workflow";
            if (!await MayResetAsync(u, c, record, user, ct)) throw new WorkflowException("notAllowed", $"You are not allowed to “{label}”.");
            var to = ResetTarget(c);
            if (record.State == to.Id && record.Status != InstanceStatus.OutOfSync && (await RunningAboutAsync(u, record, reset, ct)).Count == 0)
                throw new WorkflowException("nothingToReset", $"The record is already in “{to.Label}” with nothing running.");
            input ??= [];
            if (reset.Input?.Form is { } form)
            {
                var errors = Options.FormValidator.Validate(form, input, u.Today);
                if (errors.Count > 0) throw new WorkflowException("invalidInput", "Some answers need attention.", errors);
            }
            var from = c.StateById(record.State);
            var trigger = new TriggerInfo
            {
                Type = "reset", Field = c.Lifecycle.Field, From = from?.Key, To = to.Key, ActorType = user.Type, ActorId = user.Id, ActorName = user.Name,
                At = Values.FormatInstant(u.Now),
            };
            foreach (var run in await RunningAboutAsync(u, record, reset, ct))
                await CancelInstanceAsync(u, run, user, $"The workflow was reset by {user.Name ?? user.Id}.", ct);
            var runExitEnter = reset.RunExitEnter == true;
            if (from is not null && runExitEnter) await ExitStateAsync(u, c, record, from, user, trigger, ct);
            else foreach (var b in await u.S.ListBookmarksAsync(record.Id, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
            await WriteStateAsync(u, c, entity, id, to, user, 0, record.Id, ct);
            record.State = to.Id;
            record.StateEnteredAt = u.Now;
            record.Status = to.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active;
            record.EndedAt = to.IsTerminal ? u.Now : null;
            record.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(record, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "lifecycle", Action = "reset", SubjectType = "record", SubjectId = id, Entity = entity, EntityId = id, InstanceId = record.Id,
                DefinitionId = c.DefinitionId, Actor = user, CorrelationId = record.CorrelationId,
                Before = new Dictionary<string, object?> { ["state"] = from?.Key }, After = new Dictionary<string, object?> { ["state"] = to.Key },
                Data = new Dictionary<string, object?> { ["label"] = label, ["input"] = input },
            }, ct);
            u.Log?.Add("lifecycle", "reset", id, $"{from?.Label ?? "?"} → {to.Label} ({label})");
            await QueueLifecycleEventAsync(u, c, entity, id, from?.Key, to.Key, null, user, 0, record.CorrelationId, ct);
            // The reset steps read the answers (input.*) and may change this record and related ones.
            await StartRunAsync(u, record, c, "reset", "reset", trigger, user, input, ct);
            if (record.State == to.Id)
            {
                if (runExitEnter) await EnterStateAsync(u, c, record, to, user, trigger, 0, 0, ct, input);
                else if (!to.IsTerminal) await PlanStateTimersAsync(u, c, record, to, ct);
            }
            return await RecordViewAsync(u, entity, id, user, ct);
        }, ct);
    }

    /// <summary>The state a reset goes back to: <c>reset.to</c>, else the initial state.</summary>
    internal static State ResetTarget(CompiledDefinition c) =>
        c.Lifecycle.Reset?.To is { } to && c.StateById(to) is { } st ? st : c.InitialState;

    /// <summary>Whether the viewer may reset the record's workflow (it has a reset and they are in its <c>who</c>).</summary>
    internal async Task<bool> MayResetAsync(Uow u, CompiledDefinition c, InstanceRecord record, WorkflowActor actor, CancellationToken ct) =>
        c.Lifecycle.Reset is { } reset && await IsAmongAsync(u.Scope(record), reset.Who, actor, ct);

    /// <summary>What a reset stops: the record's unfinished runs, and the automations about the record unless the reset keeps them.</summary>
    internal async Task<List<InstanceRecord>> RunningAboutAsync(Uow u, InstanceRecord record, LifecycleReset reset, CancellationToken ct)
    {
        var running = new List<InstanceRecord>();
        foreach (var run in await u.S.QueryInstancesAsync(new InstanceQuery { ParentId = record.Id, Kinds = [InstanceKinds.Run], Take = 1000 }, ct))
            if (!InstanceStatus.IsFinished(run.Status) && !u.Driving.Contains(run.Id)) running.Add(run);
        if (reset.CancelAutomations != false)
            foreach (var a in await u.S.QueryInstancesAsync(new InstanceQuery { Entity = record.Entity, EntityId = record.EntityId, Kinds = [InstanceKinds.Automation], Take = 1000 }, ct))
                if (!InstanceStatus.IsFinished(a.Status) && !u.Driving.Contains(a.Id)) running.Add(a);
        return running;
    }

    /// <summary>
    /// Observe mode (spec §6): an outside write moved the field. It is recorded as the transition from the
    /// state the lifecycle agrees with to the new value when one exists (its steps run). Otherwise the
    /// instance becomes <c>outOfSync</c> (the state's runs are cancelled and its timers dropped) until the
    /// value returns to that state or a write reaches a state a transition leads to.
    /// </summary>
    internal async Task ObserveWriteAsync(Uow u, CompiledDefinition c, EntityChange change, FieldChange fc, CancellationToken ct)
    {
        // A record that existed before the lifecycle starts from the value it had before this write.
        var record = await EnsureRecordInstanceAsync(u, c, change.Entity, change.EntityId, ct, initialValue: fc.From);
        if (record.Version != c.Version) c = await CompiledAsync(u, record.DefinitionId, record.Version, ct);
        var lc = c.Lifecycle;
        var to = c.StateByKey(fc.To);
        var from = c.StateById(record.State);
        var tr = to is null || from is null ? null : lc.Transitions.FirstOrDefault(t => t.To == to.Id && t.From.Allows(lc, from.Id, t.To));
        if (to is not null && from?.Id == to.Id && record.Status != InstanceStatus.OutOfSync) return;
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "writeObserved", SubjectType = "record", SubjectId = change.EntityId, Entity = change.Entity, EntityId = change.EntityId,
            InstanceId = record.Id, DefinitionId = c.DefinitionId, Actor = change.Actor, CorrelationId = record.CorrelationId,
            Before = new Dictionary<string, object?> { ["state"] = fc.From }, After = new Dictionary<string, object?> { ["state"] = fc.To },
            Data = new Dictionary<string, object?> { ["transition"] = tr?.Key },
        }, ct);
        if (to is not null && from?.Id == to.Id)
        {
            // Back in the state the lifecycle agrees with: in sync again.
            await FollowStateAsync(u, c, record, to, ct);
            return;
        }
        if (from is null && to is not null)
        {
            // Out of sync on a value no state matched: any known state is valid again.
            await FollowStateAsync(u, c, record, to, ct);
            return;
        }
        if (tr is null || from is null || to is null)
        {
            await MarkOutOfSyncAsync(u, c, record, from, fc.To, ct);
            return;
        }
        var trigger = new TriggerInfo
        {
            Type = "transition", Field = lc.Field, From = from.Key, To = to.Key, Transition = tr.Key, ActorType = change.ActorType, ActorId = change.ActorId,
            ActorName = change.ActorName, At = Values.FormatInstant(u.Now), EventId = change.EventId,
        };
        await ExitStateAsync(u, c, record, from, change.Actor, trigger, ct);
        record.State = to.Id;
        record.StateEnteredAt = u.Now;
        record.Status = to.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active;
        record.EndedAt = to.IsTerminal ? u.Now : null;
        record.UpdatedAt = u.Now;
        await u.S.UpdateInstanceAsync(record, ct);
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "transitioned", SubjectType = "record", SubjectId = change.EntityId, Entity = change.Entity, EntityId = change.EntityId,
            InstanceId = record.Id, DefinitionId = c.DefinitionId, Actor = WorkflowActor.System, CorrelationId = record.CorrelationId,
            Before = new Dictionary<string, object?> { ["state"] = from.Key }, After = new Dictionary<string, object?> { ["state"] = to.Key },
            Data = new Dictionary<string, object?> { ["transition"] = tr.Key, ["label"] = tr.Label, ["observed"] = true },
        }, ct);
        await QueueLifecycleEventAsync(u, c, change.Entity, change.EntityId, from.Key, to.Key, tr.Key, change.Actor, change.Depth, record.CorrelationId, ct);
        await StartRunAsync(u, record, c, $"transition:{tr.Id}", $"transition:{tr.Id}", trigger, change.Actor, null, ct);
        await EnterStateAsync(u, c, record, to, change.Actor, trigger, change.Depth, 0, ct);
    }
}
