using System.Text.Json;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

public sealed partial class WorkflowEngine
{
    /// <summary>How long a claimed outbox row or timer stays locked to one worker.</summary>
    public TimeSpan ClaimLock { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Processes committed change events and lifecycle events, and sends queued notifications. Returns how many rows it handled.</summary>
    public async Task<int> ProcessOutboxAsync(int batch = 50, CancellationToken ct = default)
    {
        IReadOnlyList<OutboxRecord> rows;
        await using (var s = await Options.Store.BeginAsync(ct))
        {
            rows = await s.ClaimOutboxAsync(Now, Now + ClaimLock, batch, ct);
            await s.CommitAsync(ct);
        }
        foreach (var row in rows.OrderBy(r => r.CreatedAt))
        {
            try
            {
                if (row.Kind == OutboxKinds.Notify)
                {
                    var n = JsonSerializer.Deserialize<WorkflowNotification>(row.Payload, Definitions.JsonOptions)!;
                    await Options.Notifications.SendAsync(n, ct);
                    await MarkOutboxAsync(row, null, ct);
                    continue;
                }
                await RunAsync(async u =>
                {
                    if (row.Kind == OutboxKinds.Change) await ProcessChangeAsync(u, EntityChange.FromJson(row.Payload), ct);
                    else if (row.Kind == OutboxKinds.Lifecycle) await ProcessLifecycleEventAsync(u, JsonSerializer.Deserialize<LifecycleEvent>(row.Payload, Definitions.JsonOptions)!, ct);
                    row.ProcessedAt = u.Now;
                    row.LockedUntil = null;
                    row.Error = null;
                    await u.S.UpdateOutboxAsync(row, ct);
                }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await MarkOutboxAsync(row, e.Message, ct);
            }
        }
        return rows.Count;
    }

    private async Task MarkOutboxAsync(OutboxRecord row, string? error, CancellationToken ct)
    {
        await using var s = await Options.Store.BeginAsync(ct);
        row.Attempts++;
        if (error is null)
        {
            row.ProcessedAt = Now;
            row.LockedUntil = null;
            row.Error = null;
        }
        else
        {
            row.Error = error;
            // Back off: 1, 2, 4… minutes, up to an hour; after 10 attempts the row stays parked for an admin.
            row.LockedUntil = row.Attempts >= 10 ? DateTimeOffset.MaxValue : Now + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, row.Attempts - 1)));
        }
        await s.UpdateOutboxAsync(row, ct);
        await s.CommitAsync(ct);
    }

    // ── Change events ─────────────────────────────────────────────────────

    internal async Task ProcessChangeAsync(Uow u, EntityChange change, CancellationToken ct)
    {
        // 1. Lifecycles of this entity.
        foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Type == "lifecycle" && t.Entity == change.Entity).ToList())
        {
            var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
            if (change.Kind == ChangeKinds.Created)
            {
                await EnsureRecordInstanceAsync(u, c, change.Entity, change.EntityId, ct, change.Actor);
                continue;
            }
            var record = (await u.S.QueryInstancesAsync(new InstanceQuery { DefinitionId = c.DefinitionId, Entity = change.Entity, EntityId = change.EntityId, Kinds = [InstanceKinds.Record], Take = 5 }, ct))
                .FirstOrDefault(i => i.Status != InstanceStatus.Cancelled);
            if (change.Kind == ChangeKinds.Deleted)
            {
                if (record is not null) await CancelInstanceAsync(u, record, change.Actor, "The record was deleted.", ct);
                continue;
            }
            var field = c.Lifecycle.Field;
            if (change.Fields.TryGetValue(field, out var fc) && change.EngineFields?.Contains(field) != true)
            {
                // Enforce mode refuses outside writes before commit; anything that still arrives is observed.
                await ObserveWriteAsync(u, c, change, fc, ct);
            }
            else if (record is not null && record.Status == InstanceStatus.Active && record.Version > 0)
            {
                await CheckAutoWhenAsync(u, await CompiledAsync(u, record.DefinitionId, record.Version, ct), record, change.Depth, 0, ct);
            }
        }

        if (change.Kind == ChangeKinds.Deleted)
        {
            // Waiting automations about a deleted record are cancelled.
            foreach (var inst in await u.S.QueryInstancesAsync(new InstanceQuery
            {
                Entity = change.Entity, EntityId = change.EntityId, Kinds = [InstanceKinds.Automation],
                Statuses = [InstanceStatus.Waiting, InstanceStatus.Incident, InstanceStatus.Queued], Take = 1000,
            }, ct))
            {
                await CancelInstanceAsync(u, inst, change.Actor, "The record was deleted.", ct);
            }
        }
        else
        {
            // 2. Condition waits on this record.
            foreach (var b in await u.S.FindBookmarksAsync(BookmarkKinds.Condition, change.Entity, change.EntityId, null, ct))
                await ReevaluateConditionWaitAsync(u, b, ct);
            // 3. Date triggers follow changes to their date field.
            await PlanDateTriggersAsync(u, change, ct);
        }

        // 4. Automations.
        var settings = u.Settings;
        foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Entity == change.Entity && t.Type is TriggerTypes.Created or TriggerTypes.FieldChanged or TriggerTypes.Deleted).ToList())
        {
            // A change committed before this version was published belongs to the past (spec §5).
            if (change.At < t.PublishedAt) continue;
            var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
            var trig = c.Definition.Trigger!;
            var info = new TriggerInfo
            {
                Type = trig.Type, ActorType = change.ActorType, ActorId = change.ActorId, ActorName = change.ActorName, At = Values.FormatInstant(change.At), EventId = change.EventId,
            };
            switch (trig.Type)
            {
                case TriggerTypes.Created when change.Kind == ChangeKinds.Created:
                    break;
                case TriggerTypes.Deleted when change.Kind == ChangeKinds.Deleted:
                    break;
                case TriggerTypes.FieldChanged when change.Kind == ChangeKinds.Updated && trig.Field is not null && change.Fields.TryGetValue(trig.Field, out var fc):
                    if (Values.DeepEquals(fc.From, fc.To)) continue;
                    if (trig.To is { Count: > 0 } && !trig.To.Any(v => SameValue(Values.FromJson(v), fc.To))) continue;
                    if (trig.From is { Count: > 0 } && !trig.From.Any(v => SameValue(Values.FromJson(v), fc.From))) continue;
                    info.Field = trig.Field;
                    info.From = fc.From;
                    info.To = fc.To;
                    break;
                default:
                    continue;
            }
            await TryStartFromEventAsync(u, c, change.Entity, change.EntityId, change.EventId, info, change.Actor, change.Depth, change.CausationDefinitionId,
                trig, change.Kind == ChangeKinds.Deleted ? change.Snapshot : null, change.CorrelationId, settings, ct);
        }
    }

    private static bool SameValue(object? a, object? b) =>
        Conditions.Evaluate(new ConditionRule { Ref = "$", Op = "eq", Value = Operand.Of(a) }, new Single(b));

    private sealed class Single(object? v) : IValueContext
    {
        public object? Read(string path) => v;
        public string Today => "1970-01-01";
    }

    /// <summary>Applies the start rules shared by every event trigger: pause, loop protection, dedupe and the extra condition.</summary>
    private async Task TryStartFromEventAsync(Uow u, CompiledDefinition c, string entity, string entityId, string eventId, TriggerInfo info, WorkflowActor actor,
        int depth, string? causationDefinitionId, Trigger trig, Dictionary<string, object?>? snapshot, string? correlationId, Settings.GlobalSettings settings, CancellationToken ct)
    {
        AuditEntry Suppressed(string reason) => new()
        {
            Category = "trigger", Action = reason == "paused" ? "paused" : "suppressed", SubjectType = "definition", SubjectId = c.DefinitionId, DefinitionId = c.DefinitionId,
            Entity = entity, EntityId = entityId, Data = new Dictionary<string, object?> { ["reason"] = reason, ["eventId"] = eventId, ["depth"] = (double)depth },
        };
        if (settings.IsPaused(entity))
        {
            await u.AuditAsync(Suppressed("paused"), ct);
            return;
        }
        if (depth >= settings.MaxChainDepth)
        {
            await u.AuditAsync(Suppressed("maxChainDepth"), ct);
            return;
        }
        if (causationDefinitionId == c.DefinitionId && trig.Retrigger != true) return;
        if (!await u.S.TryClaimTriggerEventAsync(c.DefinitionId, eventId, ct)) return;
        if (trig.When is not null)
        {
            var scope = new ValueScope(u.S.Entities, Registry)
            {
                Entity = entity, EntityId = entityId, EntityOverride = snapshot, Trigger = info, Now = u.Now, Today = u.Today,
            };
            if (!await scope.ConditionAsync(trig.When, ct)) return;
        }
        await StartAutomationAsync(u, c, entity, entityId, info, actor, depth, null, null, null, correlationId, ct, snapshot: snapshot);
    }

    internal async Task ProcessLifecycleEventAsync(Uow u, LifecycleEvent ev, CancellationToken ct)
    {
        foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Lifecycle == ev.Lifecycle && t.Type is TriggerTypes.StateEntered or TriggerTypes.TransitionApplied).ToList())
        {
            if (ev.At < t.PublishedAt) continue;
            var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
            var trig = c.Definition.Trigger!;
            if (trig.Type == TriggerTypes.StateEntered && trig.States?.Contains(ev.ToState) != true) continue;
            if (trig.Type == TriggerTypes.TransitionApplied && (ev.Transition is null || trig.Transitions?.Contains(ev.Transition) != true)) continue;
            var info = new TriggerInfo
            {
                Type = trig.Type, Field = null, From = ev.FromState, To = ev.ToState, Transition = ev.Transition, ActorType = ev.ActorType, ActorId = ev.ActorId,
                ActorName = ev.ActorName, At = Values.FormatInstant(ev.At), EventId = ev.EventId,
            };
            await TryStartFromEventAsync(u, c, ev.Entity, ev.EntityId, ev.EventId, info, ev.Actor, ev.Depth, null, trig, null, ev.CorrelationId, u.Settings, ct);
        }
    }

    private async Task ReevaluateConditionWaitAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        if (b.InstanceId is null || b.TokenId is null) return;
        var x = await LoadExecAsync(u, b.InstanceId, ct);
        var t = x.S.Token(b.TokenId);
        if (t is null || t.Status != TokenStatus.Waiting || x.C.Step(b.NodeId!) is not WaitStep w) return;
        if (!await x.Scope(t).ConditionAsync(w.Until.When, ct)) return;
        await ClearWaitsAsync(x, t, ct);
        x.S.SetOutput(w.Id, "timedOut", false);
        Resume(t);
        await CompleteWaitingStepAsync(x, t, w, ct);
        Advance(t);
        await DriveAsync(x, ct);
    }

    private async Task PlanDateTriggersAsync(Uow u, EntityChange change, CancellationToken ct)
    {
        foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Type == TriggerTypes.DateReached && t.Entity == change.Entity).ToList())
        {
            if (change.Kind != ChangeKinds.Created && (t.Field is null || !change.Fields.ContainsKey(t.Field))) continue;
            var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
            await PlanDateTriggerAsync(u, c, change.Entity, change.EntityId, ct);
        }
    }

    internal async Task PlanDateTriggerAsync(Uow u, CompiledDefinition c, string entity, string id, CancellationToken ct)
    {
        var trig = c.Definition.Trigger!;
        foreach (var old in (await u.S.FindDefinitionBookmarksAsync(c.DefinitionId, BookmarkKinds.DateReached, ct)).Where(b => b.EntityId == id))
            await u.S.RemoveBookmarkAsync(old.Id, ct);
        var snap = await u.S.Entities.GetAsync(entity, id, ct);
        var value = snap?[trig.Field!];
        var x = new Exec(u, new InstanceRecord(), c, new RunState());
        var due = DateWaitDue(x, value, trig.Offset, trig.At);
        if (due is null || due <= u.Now) return;
        await u.S.AddBookmarkAsync(new BookmarkRecord
        {
            Id = NewId(), Kind = BookmarkKinds.DateReached, Purpose = "dateReached", DefinitionId = c.DefinitionId, Entity = entity, EntityId = id, DueAt = due,
            Payload = Values.Serialize(new Dictionary<string, object?> { ["version"] = (double)c.Version, ["value"] = value }), CreatedAt = u.Now,
        }, ct);
    }

    // ── Timers ────────────────────────────────────────────────────────────

    /// <summary>Fires due timers: waits, task deadlines, reminders, escalations, retries, automatic transitions, SLAs, schedules and date triggers.</summary>
    public async Task<int> ProcessTimersAsync(int batch = 50, CancellationToken ct = default)
    {
        IReadOnlyList<BookmarkRecord> due;
        await using (var s = await Options.Store.BeginAsync(ct))
        {
            due = await s.ClaimDueBookmarksAsync(Now, Now + ClaimLock, batch, ct);
            await s.CommitAsync(ct);
        }
        foreach (var b in due.OrderBy(b => b.DueAt))
        {
            try
            {
                await RunAsync(u => FireAsync(u, b.Id, ct), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Leave it locked for a while and record why; the next claim after the lock retries it.
                await RunAsync(async u =>
                {
                    await u.AuditAsync(new AuditEntry
                    {
                        Category = "instance", Action = "timerFailed", SubjectType = "bookmark", SubjectId = b.Id, InstanceId = b.InstanceId, DefinitionId = b.DefinitionId,
                        Entity = b.Entity, EntityId = b.EntityId, Data = new Dictionary<string, object?> { ["purpose"] = b.Purpose, ["error"] = e.Message },
                    }, ct);
                }, ct);
            }
        }
        return due.Count;
    }

    internal async Task FireAsync(Uow u, string bookmarkId, CancellationToken ct)
    {
        var b = await u.S.GetBookmarkAsync(bookmarkId, ct);
        if (b is null) return;
        // Reminders and escalations don't end the task, so their own bookmark is all that goes.
        await u.S.RemoveBookmarkAsync(b.Id, ct);
        switch (b.Purpose)
        {
            case "schedule": await RunScheduleAsync(u, b, ct); return;
            case "dateReached": await RunDateTriggerAsync(u, b, ct); return;
            case "reminder": await TaskReminderAsync(u, b, ct); return;
            case "escalation": await TaskEscalationAsync(u, b, ct); return;
            case "due": await TaskTimedOutAsync(u, b, ct); return;
        }
        if (b.InstanceId is null) return;
        var x = await LoadExecAsync(u, b.InstanceId, ct);
        if (InstanceStatus.IsFinished(x.I.Status)) return;
        if (b.Purpose.StartsWith("auto:", StringComparison.Ordinal))
        {
            var tr = x.C.TransitionById(b.Purpose[5..]);
            if (tr is null || x.I.State is null || !tr.From.Allows(x.C.Lifecycle, x.I.State, tr.To)) return;
            try
            {
                await ApplyTransitionCoreAsync(u, x.C.Definition.Key, x.I.Entity!, x.I.EntityId!, tr.Key, [], WorkflowActor.System, isPerson: false, auto: true, 0, ct);
            }
            catch (WorkflowException e) when (e.Code is "guardFailed" or "notInFromState")
            {
                await u.AuditAsync(new AuditEntry
                {
                    Category = "lifecycle", Action = "autoSkipped", SubjectType = "record", SubjectId = x.I.EntityId!, Entity = x.I.Entity, EntityId = x.I.EntityId,
                    InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId, Data = new Dictionary<string, object?> { ["transition"] = tr.Key, ["reason"] = e.Code },
                }, ct);
            }
            return;
        }
        if (b.Purpose.StartsWith("sla:", StringComparison.Ordinal))
        {
            var state = x.C.StateById(b.Purpose[4..]);
            if (state is null || x.I.State != state.Id) return;
            await u.AuditAsync(new AuditEntry
            {
                Category = "lifecycle", Action = "slaBreached", SubjectType = "record", SubjectId = x.I.EntityId!, Entity = x.I.Entity, EntityId = x.I.EntityId,
                InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId, Data = new Dictionary<string, object?> { ["state"] = state.Key, ["within"] = state.Sla?.Within },
            }, ct);
            var trigger = new TriggerInfo { Type = "sla", Field = x.C.Lifecycle.Field, To = state.Key, At = Values.FormatInstant(u.Now) };
            await StartRunAsync(u, x.I, x.C, $"sla:{state.Id}", $"sla:{state.Id}", trigger, WorkflowActor.System, null, ct);
            return;
        }
        var t = b.TokenId is null ? null : x.S.Token(b.TokenId);
        if (t is null || t.Status != TokenStatus.Waiting) return;
        var step = b.NodeId is null ? null : x.C.Step(b.NodeId);
        if (step is null) return;
        switch (b.Purpose)
        {
            case "wait":
                x.S.SetOutput(step.Id, "timedOut", false);
                Resume(t);
                await CompleteWaitingStepAsync(x, t, step, ct);
                Advance(t);
                break;
            case "timeout":
                await ClearWaitsAsync(x, t, ct);
                x.S.SetOutput(step.Id, "timedOut", true);
                Resume(t);
                await CompleteWaitingStepAsync(x, t, step, ct);
                if (step is WaitStep { Until.OnTimeout: not null }) t.Frames.Add(new Frame { List = $"{step.Id}/onTimeout" });
                else Advance(t);
                break;
            case "retry":
                Resume(t);
                await u.AuditAsync(StepAudit(x, step, "retrying", new Dictionary<string, object?> { ["attempt"] = (double)t.Top.Attempt }), ct);
                break;
            default:
                return;
        }
        await DriveAsync(x, ct);
    }

    private async Task RunScheduleAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        var rec = b.DefinitionId is null ? null : await u.S.GetDefinitionAsync(b.DefinitionId, ct);
        if (rec is null || rec.Version == 0 || rec.Status == DefinitionStatus.Archived) return;
        var c = await CompiledAsync(u, rec.Id, rec.Version, ct);
        var trig = c.Definition.Trigger;
        if (trig?.Type != TriggerTypes.Schedule || !Cron.TryParse(trig.Cron, out var cron, out _)) return;
        // Plan the next run first, so a failure below doesn't stop the schedule.
        var next = cron!.Next(u.Now, u.Calendar.Zone);
        if (next is not null)
            await u.S.AddBookmarkAsync(new BookmarkRecord { Id = NewId(), Kind = BookmarkKinds.Schedule, Purpose = "schedule", DefinitionId = rec.Id, DueAt = next, CreatedAt = u.Now }, ct);
        var eventId = $"schedule:{b.DueAt?.ToUnixTimeSeconds()}";
        var info = new TriggerInfo { Type = TriggerTypes.Schedule, ActorType = "system", At = Values.FormatInstant(u.Now), EventId = eventId };
        if (trig.ForEach is null)
        {
            if (await u.S.TryClaimTriggerEventAsync(c.DefinitionId, eventId, ct))
                await StartAutomationAsync(u, c, null, null, info, WorkflowActor.System, 0, null, null, null, null, ct);
            return;
        }
        if (c.Definition.Entity is null || u.Settings.IsPaused(c.Definition.Entity)) return;
        var limit = Math.Clamp(trig.ForEach.Limit, 1, 500);
        var started = 0;
        foreach (var id in await u.S.Entities.ListIdsAsync(c.Definition.Entity, 5000, ct))
        {
            if (started >= limit) break;
            if (trig.ForEach.When is not null)
            {
                var scope = new ValueScope(u.S.Entities, Registry) { Entity = c.Definition.Entity, EntityId = id, Trigger = info, Now = u.Now, Today = u.Today };
                if (!await scope.ConditionAsync(trig.ForEach.When, ct)) continue;
            }
            if (!await u.S.TryClaimTriggerEventAsync(c.DefinitionId, $"{eventId}:{id}", ct)) continue;
            await StartAutomationAsync(u, c, c.Definition.Entity, id, info, WorkflowActor.System, 0, null, null, null, null, ct);
            started++;
        }
    }

    private async Task RunDateTriggerAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        var rec = b.DefinitionId is null ? null : await u.S.GetDefinitionAsync(b.DefinitionId, ct);
        if (rec is null || rec.Version == 0 || rec.Status == DefinitionStatus.Archived || b.Entity is null || b.EntityId is null) return;
        var c = await CompiledAsync(u, rec.Id, rec.Version, ct);
        var trig = c.Definition.Trigger;
        if (trig?.Type != TriggerTypes.DateReached) return;
        var payload = Values.FromJson(b.Payload ?? "{}") as Dictionary<string, object?>;
        var info = new TriggerInfo { Type = TriggerTypes.DateReached, Field = trig.Field, To = payload?.GetValueOrDefault("value"), ActorType = "system", At = Values.FormatInstant(u.Now), EventId = $"date:{b.Id}" };
        await TryStartFromEventAsync(u, c, b.Entity, b.EntityId, $"date:{b.Id}", info, WorkflowActor.System, 0, null, trig, null, null, u.Settings, ct);
    }

    // ── Signals and manual starts ─────────────────────────────────────────

    /// <summary>The app raises a named event (spec §5 <c>signal</c>, §7 wait for signal), optionally about one record.</summary>
    public Task<int> SignalAsync(string signal, string? entity, string? entityId, Dictionary<string, object?>? data, WorkflowActor? actor = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            if (Registry.Signal(signal) is null) throw new WorkflowException("unknownSignal", $"'{signal}' is not a registered signal.");
            var resumed = 0;
            foreach (var b in await u.S.FindBookmarksAsync(BookmarkKinds.Signal, entity, entityId, signal, ct))
            {
                if (b.InstanceId is null || b.TokenId is null) continue;
                var x = await LoadExecAsync(u, b.InstanceId, ct);
                var t = x.S.Token(b.TokenId);
                if (t is null || t.Status != TokenStatus.Waiting || b.NodeId is null || x.C.Step(b.NodeId) is not { } step) continue;
                await ClearWaitsAsync(x, t, ct);
                x.S.SetOutput(step.Id, "timedOut", false);
                x.S.SetOutput(step.Id, "data", data);
                Resume(t);
                await CompleteWaitingStepAsync(x, t, step, ct);
                Advance(t);
                await DriveAsync(x, ct);
                resumed++;
            }
            var eventId = NewId();
            foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Type == TriggerTypes.Signal && t.Signal == signal).ToList())
            {
                var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
                if (c.Definition.Entity is not null && (entity != c.Definition.Entity || entityId is null)) continue;
                var info = new TriggerInfo
                {
                    Type = TriggerTypes.Signal, ActorType = actor?.Type ?? "system", ActorId = actor?.Id, ActorName = actor?.Name, At = Values.FormatInstant(u.Now),
                    EventId = eventId, Data = data,
                };
                await TryStartFromEventAsync(u, c, entity ?? "", entityId ?? "", eventId, info, actor ?? WorkflowActor.System, WorkflowWriteScope.Current?.Depth ?? 0,
                    null, c.Definition.Trigger!, null, null, u.Settings, ct);
                resumed++;
            }
            return resumed;
        }, ct);

    /// <summary>A person starts a manual-trigger automation from a record (spec §5 <c>manual</c>).</summary>
    public Task<InstanceView> StartManualAsync(string definitionKey, string? entity, string? entityId, Dictionary<string, object?>? input, WorkflowActor user, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var rec = await u.S.GetDefinitionByKeyAsync(definitionKey, ct);
            if (rec is null || rec.Version == 0 || rec.Status == DefinitionStatus.Archived) throw new WorkflowException("unknownWorkflow", $"There is no published workflow '{definitionKey}'.");
            var c = await CompiledAsync(u, rec.Id, rec.Version, ct);
            var trig = c.Definition.Trigger;
            if (trig?.Type != TriggerTypes.Manual) throw new WorkflowException("notManual", $"'{c.Definition.Title}' cannot be started by hand.");
            if (c.Definition.Entity is not null && (entity != c.Definition.Entity || entityId is null)) throw new WorkflowException("noRecord", $"'{c.Definition.Title}' starts from a {c.Definition.Entity}.");
            var scope = u.Scope(null, null, null, null, entity, entityId);
            if (trig.Who is { Count: > 0 })
            {
                var allowed = await ResolveAssigneesAsync(scope, trig.Who, null, user.Id, ct);
                var byRole = user.Roles is { Count: > 0 } roles && trig.Who.Any(w => w.Role is not null && roles.Contains(w.Role));
                if (!user.IsUser || (!allowed.Contains(user.Id!) && !byRole)) throw new WorkflowException("notAllowed", $"You are not allowed to start '{c.Definition.Title}'.");
            }
            input ??= [];
            if (trig.Form is { } form)
            {
                var errors = Options.FormValidator.Validate(form, input, u.Today);
                if (errors.Count > 0) throw new WorkflowException("invalidInput", "Some answers need attention.", errors);
            }
            var info = new TriggerInfo { Type = TriggerTypes.Manual, ActorType = user.Type, ActorId = user.Id, ActorName = user.Name, At = Values.FormatInstant(u.Now), EventId = NewId(), Data = input };
            var inst = await StartAutomationAsync(u, c, entity, entityId, info, user, 0, input, null, null, null, ct)
                       ?? throw new WorkflowException("alreadyRunning", $"'{c.Definition.Title}' is already running for this record.");
            return await InstanceViewAsync(u, inst.Id, ct);
        }, ct);
}
