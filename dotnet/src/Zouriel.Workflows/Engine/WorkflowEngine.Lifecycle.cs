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
    internal async Task<InstanceRecord> EnsureRecordInstanceAsync(Uow u, CompiledDefinition c, string entity, string id, CancellationToken ct, WorkflowActor? actor = null)
    {
        var existing = (await u.S.QueryInstancesAsync(new InstanceQuery
        {
            DefinitionId = c.DefinitionId, Entity = entity, EntityId = id, Kinds = [InstanceKinds.Record], Take = 10,
        }, ct)).Where(i => i.Status != InstanceStatus.Cancelled).OrderByDescending(i => i.StartedAt).FirstOrDefault();
        if (existing is not null) return existing;
        var snap = await u.S.Entities.GetAsync(entity, id, ct) ?? throw new WorkflowException("noRecord", $"{entity} {id} does not exist.");
        var lc = c.Lifecycle;
        var value = snap[lc.Field];
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

    private async Task WriteStateAsync(Uow u, CompiledDefinition c, string entity, string id, State state, WorkflowActor actor, int depth, string instanceId, CancellationToken ct)
    {
        using var scope = WorkflowWriteScope.Begin(actor, depth, instanceId, c.DefinitionId);
        scope.AllowOwned(entity, id, c.Lifecycle.Field);
        await u.S.Entities.SetFieldsAsync(entity, id, new Dictionary<string, object?> { [c.Lifecycle.Field] = state.Key }, ct);
    }

    // ── Applying transitions ──────────────────────────────────────────────

    /// <summary>A person applies a transition to a record (spec §6). Everything happens in one unit of work.</summary>
    public Task<RecordView> ApplyTransitionAsync(string entity, string id, string transition, Dictionary<string, object?>? input, WorkflowActor user, string? lifecycle = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var c = lifecycle is null ? await LifecycleForAsync(u, entity, null, null, ct) : await RequireLifecycleAsync(u, lifecycle, entity, ct);
            if (c is null) throw new WorkflowException("unknownLifecycle", $"{entity} has no published lifecycle.");
            await ApplyTransitionCoreAsync(u, c.Definition.Key, entity, id, transition, input ?? [], user, isPerson: user.IsUser, auto: false, 0, ct);
            return await RecordViewAsync(u, entity, id, user, ct);
        }, ct);

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
        if (isPerson && !await MayApplyAsync(u, scope, tr, actor, record, ct))
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
            Data = new Dictionary<string, object?> { ["transition"] = tr.Key, ["label"] = tr.Label, ["input"] = AuditWriter.Mask(input, SensitiveInputs(u)) },
        }, ct);
        u.Log?.Add("lifecycle", "transitioned", id, $"{from.Label} → {to.Label} ({tr.Label})");
        await QueueLifecycleEventAsync(u, c, entity, id, from.Key, to.Key, tr.Key, actor, depth, record.CorrelationId, ct);
        await StartRunAsync(u, record, c, $"transition:{tr.Id}", $"transition:{tr.Id}", trigger, actor, input, ct);
        await EnterStateAsync(u, c, record, to, actor, trigger, depth, chain, ct, input);
    }

    internal async Task<bool> MayApplyAsync(Uow u, ValueScope scope, Transition tr, WorkflowActor actor, InstanceRecord record, CancellationToken ct)
    {
        if (!actor.IsUser || tr.Who is not { Count: > 0 }) return false;
        var allowed = await ResolveAssigneesAsync(scope, tr.Who, null, actor.Id, ct);
        if (allowed.Contains(actor.Id!)) return true;
        // Roles the caller carries (from their login) count too, so apps need not list every member.
        return actor.Roles is { Count: > 0 } roles && tr.Who.Any(w => w.Role is not null && roles.Contains(w.Role));
    }

    private async Task ExitStateAsync(Uow u, CompiledDefinition c, InstanceRecord record, State from, WorkflowActor actor, TriggerInfo trigger, CancellationToken ct)
    {
        foreach (var b in await u.S.ListBookmarksAsync(record.Id, ct))
            if (b.Purpose.StartsWith("auto:", StringComparison.Ordinal) || b.Purpose.StartsWith("sla:", StringComparison.Ordinal)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        if (from.KeepRunsOnExit != true)
        {
            foreach (var run in await u.S.QueryInstancesAsync(new InstanceQuery { ParentId = record.Id, Kinds = [InstanceKinds.Run], Take = 1000 }, ct))
            {
                if (InstanceStatus.IsFinished(run.Status)) continue;
                if (run.Reason == $"onEnter:{from.Id}" || run.Reason == $"sla:{from.Id}")
                    await CancelInstanceAsync(u, run, WorkflowActor.System, $"The record left “{from.Label}”.", ct);
            }
        }
        await StartRunAsync(u, record, c, $"onExit:{from.Id}", $"onExit:{from.Id}", trigger, actor, null, ct);
    }

    private async Task EnterStateAsync(Uow u, CompiledDefinition c, InstanceRecord record, State to, WorkflowActor actor, TriggerInfo trigger, int depth, int chain,
        CancellationToken ct, Dictionary<string, object?>? input = null)
    {
        // onEnter steps can read the answers of the transition that entered the state (input.*).
        await StartRunAsync(u, record, c, $"onEnter:{to.Id}", $"onEnter:{to.Id}", trigger, actor, input, ct);
        if (to.IsTerminal) return;
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
                Id = NewId(), InstanceId = record.Id, Kind = BookmarkKinds.Timer, Purpose = $"auto:{t.Id}", DueAt = u.Calendar.Add(u.Now, d, working),
                Entity = record.Entity, EntityId = record.EntityId, DefinitionId = record.DefinitionId, CreatedAt = u.Now,
            }, ct);
        }
        if (state.Sla is { } sla && IsoDuration.TryParse(sla.Within, out var within))
        {
            var working = (sla.Calendar ?? c.Definition.Settings?.Calendar) == "working";
            await u.S.AddBookmarkAsync(new BookmarkRecord
            {
                Id = NewId(), InstanceId = record.Id, Kind = BookmarkKinds.Timer, Purpose = $"sla:{state.Id}", DueAt = u.Calendar.Add(u.Now, within, working),
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
        };
        await u.S.AddOutboxAsync(new OutboxRecord
        {
            Id = NewId(), Kind = OutboxKinds.Lifecycle, Payload = System.Text.Json.JsonSerializer.Serialize(ev, Definitions.JsonOptions), CreatedAt = u.Now,
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

    /// <summary>Observe mode: an outside write moved the field. Record it as a transition when one fits.</summary>
    internal async Task ObserveWriteAsync(Uow u, CompiledDefinition c, EntityChange change, FieldChange fc, CancellationToken ct)
    {
        var record = await EnsureRecordInstanceAsync(u, c, change.Entity, change.EntityId, ct);
        if (record.Version != c.Version) c = await CompiledAsync(u, record.DefinitionId, record.Version, ct);
        var lc = c.Lifecycle;
        var to = c.StateByKey(fc.To);
        var from = c.StateById(record.State);
        if (to is not null && from?.Id == to.Id) return;
        var tr = to is null || from is null ? null : lc.Transitions.FirstOrDefault(t => t.To == to.Id && t.From.Allows(lc, from.Id, t.To));
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "writeObserved", SubjectType = "record", SubjectId = change.EntityId, Entity = change.Entity, EntityId = change.EntityId,
            InstanceId = record.Id, DefinitionId = c.DefinitionId, Actor = change.Actor, CorrelationId = record.CorrelationId,
            Before = new Dictionary<string, object?> { ["state"] = fc.From }, After = new Dictionary<string, object?> { ["state"] = fc.To },
            Data = new Dictionary<string, object?> { ["transition"] = tr?.Key },
        }, ct);
        if (to is null || tr is null || from is null)
        {
            record.Status = to is null ? InstanceStatus.OutOfSync : to.IsTerminal ? InstanceStatus.Completed : InstanceStatus.Active;
            if (to is not null)
            {
                // No transition fits, but the value is a known state: follow it so the record stays in sync.
                foreach (var b in await u.S.ListBookmarksAsync(record.Id, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
                record.State = to.Id;
                record.StateEnteredAt = u.Now;
                if (!to.IsTerminal) await PlanStateTimersAsync(u, c, record, to, ct);
            }
            record.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(record, ct);
            if (to is null)
                await u.AuditAsync(new AuditEntry
                {
                    Category = "lifecycle", Action = "outOfSync", SubjectType = "record", SubjectId = change.EntityId, Entity = change.Entity, EntityId = change.EntityId,
                    InstanceId = record.Id, DefinitionId = c.DefinitionId, Data = new Dictionary<string, object?> { ["value"] = fc.To },
                }, ct);
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
