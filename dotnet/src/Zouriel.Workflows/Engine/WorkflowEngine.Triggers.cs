using System.Text.Json;
using Microsoft.Extensions.Logging;
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

    /// <summary>How many times a timer may fail before it is parked for an admin (default 10, like outbox rows).</summary>
    public int MaxTimerAttempts { get; set; } = 10;

    /// <summary>
    /// Processes committed change events and lifecycle events, and sends queued notifications. Returns how
    /// many rows it claimed. Each automation an event starts runs in its own unit of work: one failing
    /// definition is audited (<c>trigger.failed</c>) and does not hold up the others; trigger claims keep a
    /// reprocessed event from starting anything twice.
    /// </summary>
    public async Task<int> ProcessOutboxAsync(int batch = 50, CancellationToken ct = default)
    {
        IReadOnlyList<OutboxRecord> rows;
        await using (var s = await Options.Store.BeginAsync(ct))
        {
            rows = await s.ClaimOutboxAsync(Now, Now + ClaimLock, batch, ct);
            await s.CommitAsync(ct);
        }
        foreach (var row in rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            // The claim covers the whole batch; once it lapses another worker may take the rest, so stop.
            if (row.LockedUntil is { } until && Now >= until) break;
            // One record's events in the order they were written: a newer one waits while an older one is pending
            // (failed and backing off, or not yet done), so a later change is never applied before an earlier one.
            if (row.RecordKey is not null && await HasEarlierPendingAsync(row, ct))
            {
                await DeferOutboxAsync(row, ct);
                continue;
            }
            try
            {
                switch (row.Kind)
                {
                    case OutboxKinds.Notify:
                        await Options.Notifications.SendAsync(JsonSerializer.Deserialize<WorkflowNotification>(row.Payload, Definitions.JsonOptions)!, ct);
                        break;
                    case OutboxKinds.Change:
                    {
                        var change = EntityChange.FromJson(row.Payload);
                        var starts = await RunAsync(async u =>
                        {
                            await ProcessChangeAsync(u, change, ct);
                            return await ChangeStartsAsync(u, change, ct);
                        }, ct);
                        await StartEachAsync(starts, ct);
                        break;
                    }
                    case OutboxKinds.Lifecycle:
                    {
                        var ev = JsonSerializer.Deserialize<LifecycleEvent>(row.Payload, Definitions.JsonOptions)!;
                        await StartEachAsync(await RunAsync(u => LifecycleStartsAsync(u, ev, ct), ct), ct);
                        break;
                    }
                }
                await MarkOutboxAsync(row, null, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await MarkOutboxAsync(row, e.Message, ct);
            }
        }
        return rows.Count;
    }

    /// <summary>An automation an event may start, worked out in one unit and started in its own.</summary>
    internal sealed record EventStart(
        string DefinitionId, int Version, string? Entity, string? EntityId, string EventId, TriggerInfo Info, WorkflowActor Actor, int Depth,
        string? CausationDefinitionId, Dictionary<string, object?>? Snapshot, string? CorrelationId);

    private async Task StartEachAsync(IReadOnlyList<EventStart> starts, CancellationToken ct)
    {
        foreach (var start in starts)
        {
            try
            {
                await RunAsync(async u =>
                {
                    var c = await CompiledAsync(u, start.DefinitionId, start.Version, ct);
                    await TryStartFromEventAsync(u, c, start, ct);
                }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException and not WorkflowConcurrencyException)
            {
                await RunAsync(u => u.AuditAsync(new AuditEntry
                {
                    Category = "trigger", Action = "failed", SubjectType = "definition", SubjectId = start.DefinitionId, DefinitionId = start.DefinitionId,
                    Entity = start.Entity, EntityId = start.EntityId,
                    Data = new Dictionary<string, object?> { ["eventId"] = start.EventId, ["error"] = e is WorkflowException ? e.Message : $"{e.GetType().Name}: {e.Message}" },
                }, ct), ct);
            }
        }
    }

    private async Task<bool> HasEarlierPendingAsync(OutboxRecord row, CancellationToken ct)
    {
        await using var s = await Options.Store.BeginAsync(ct);
        return await s.HasEarlierPendingOutboxAsync(row, ct);
    }

    /// <summary>Leaves a row for a later round (an older row of its record is pending); not counted as an attempt.</summary>
    private async Task DeferOutboxAsync(OutboxRecord row, CancellationToken ct)
    {
        await using var s = await Options.Store.BeginAsync(ct);
        row.LockedUntil = Now + TimeSpan.FromSeconds(30);
        await s.UpdateOutboxAsync(row, ct);
        await s.CommitAsync(ct);
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

    /// <summary>
    /// Deletes housekeeping rows nobody needs any more: processed outbox rows older than
    /// <paramref name="keepOutbox"/> and trigger claims older than <paramref name="keepClaims"/> (by then no
    /// event can be delivered again). The worker calls it now and then.
    /// </summary>
    public async Task<int> CleanupAsync(TimeSpan keepOutbox, TimeSpan keepClaims, CancellationToken ct = default)
    {
        await using var s = await Options.Store.BeginAsync(ct);
        var n = await s.CleanupAsync(Now - keepOutbox, Now - keepClaims, ct);
        await s.CommitAsync(ct);
        return n;
    }

    // ── Change events ─────────────────────────────────────────────────────

    internal async Task ProcessChangeAsync(Uow u, EntityChange change, CancellationToken ct)
    {
        // 1. Lifecycles of this entity: per field, the one that applies to this record (spec §6 applies to).
        var lifecycleTriggers = (await u.S.ListTriggersAsync(ct)).Where(t => t.Type == "lifecycle" && t.Entity == change.Entity).ToList();
        if (change.Kind == ChangeKinds.Deleted)
        {
            // Every lifecycle the record was on ends with it, archived ones included.
            foreach (var gone in await LiveRecordInstancesAsync(u, change.Entity, change.EntityId, ct))
                await CancelInstanceAsync(u, gone, change.Actor, "The record was deleted.", ct);
        }
        // A record keeps the lifecycle it has. One with none yet gets it from its values like the change
        // hook decided ownership: a new record's own values, an existing record's values before this write.
        var values = change.Kind == ChangeKinds.Deleted || lifecycleTriggers.Count == 0 ? null
            : change.Kind == ChangeKinds.Created ? change.Fields.ToDictionary(kv => kv.Key, kv => kv.Value.To)
            : await ValuesBeforeAsync(u, change, ct);
        foreach (var ownedField in change.Kind == ChangeKinds.Deleted ? [] : lifecycleTriggers.Select(t => t.Field).Distinct().ToList())
        {
            var c = await LifecycleForRecordAsync(u, change.Entity, change.EntityId, ownedField, ct, values);
            if (c is null) continue;
            if (change.Kind == ChangeKinds.Created)
            {
                // A new record enters its first state: onEnter runs and stateEntered automations start (spec §6).
                if (await FindRecordInstanceAsync(u, c.DefinitionId, change.Entity, change.EntityId, ct) is null)
                    await EnsureRecordInstanceAsync(u, c, change.Entity, change.EntityId, ct, change.Actor, entering: true, depth: change.Depth);
                continue;
            }
            var record = await FindRecordInstanceAsync(u, c.DefinitionId, change.Entity, change.EntityId, ct);
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
        }
        // 3. Date triggers follow changes to their date field (and are dropped with the record).
        await PlanDateTriggersAsync(u, change, ct);
    }

    /// <summary>A changed record's values before the change: its current values with the changed fields put back.</summary>
    private static async Task<Dictionary<string, object?>> ValuesBeforeAsync(Uow u, EntityChange change, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>((await u.S.Entities.GetAsync(change.Entity, change.EntityId, ct))?.Values ?? [], StringComparer.Ordinal);
        foreach (var (k, fc) in change.Fields) values[k] = fc.From;
        return values;
    }

    /// <summary>The automations a change event matches (spec §5 created/fieldChanged/deleted).</summary>
    internal async Task<IReadOnlyList<EventStart>> ChangeStartsAsync(Uow u, EntityChange change, CancellationToken ct)
    {
        var starts = new List<EventStart>();
        // A reset taking back what a workflow did starts nothing, so an automation cannot redo it (spec §6).
        if (change.SuppressTriggers) return starts;
        foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Entity == change.Entity && t.Type is TriggerTypes.Created or TriggerTypes.FieldChanged or TriggerTypes.Deleted))
        {
            // A change committed before the workflow was first published belongs to the past (spec §5).
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
            starts.Add(new EventStart(t.DefinitionId, t.Version, change.Entity, change.EntityId, change.EventId, info, change.Actor, change.Depth,
                change.CausationDefinitionId, change.Kind == ChangeKinds.Deleted ? change.Snapshot : null, change.CorrelationId));
        }
        return starts;
    }

    private static bool SameValue(object? a, object? b) =>
        Conditions.Evaluate(new ConditionRule { Ref = "$", Op = "eq", Value = Operand.Of(a) }, new Single(b));

    private sealed class Single(object? v) : IValueContext
    {
        public object? Read(string path) => v;
        public string Today => "1970-01-01";
    }

    /// <summary>Applies the start rules shared by every event trigger: pause, loop protection, dedupe and the extra condition.</summary>
    private async Task<bool> TryStartFromEventAsync(Uow u, CompiledDefinition c, EventStart e, CancellationToken ct)
    {
        var trig = c.Definition.Trigger!;
        AuditEntry Suppressed(string reason) => new()
        {
            Category = "trigger", Action = reason == "paused" ? "paused" : "suppressed", SubjectType = "definition", SubjectId = c.DefinitionId, DefinitionId = c.DefinitionId,
            Entity = e.Entity, EntityId = e.EntityId, Data = new Dictionary<string, object?> { ["reason"] = reason, ["eventId"] = e.EventId, ["depth"] = (double)e.Depth },
        };
        if (e.Entity is not null && u.Settings.IsPaused(e.Entity))
        {
            await u.AuditAsync(Suppressed("paused"), ct);
            return false;
        }
        if (e.Depth >= u.Settings.MaxChainDepth)
        {
            await u.AuditAsync(Suppressed("maxChainDepth"), ct);
            return false;
        }
        // A workflow's own writes never start the same workflow again unless it opts in (spec §5).
        if (e.CausationDefinitionId == c.DefinitionId && trig.Retrigger != true) return false;
        if (!await u.S.TryClaimTriggerEventAsync(c.DefinitionId, e.EventId, ct)) return false;
        if (trig.When is not null)
        {
            var scope = new ValueScope(u.S.Entities, Registry)
            {
                Entity = e.Entity, EntityId = e.EntityId, EntityOverride = e.Snapshot, Trigger = e.Info, Now = u.Now, Today = u.Today,
            };
            if (!await scope.ConditionAsync(trig.When, ct)) return false;
        }
        return await StartAutomationAsync(u, c, e.Entity, e.EntityId, e.Info, e.Actor, e.Depth, null, null, null, e.CorrelationId, ct, snapshot: e.Snapshot) is not null;
    }

    /// <summary>The automations a lifecycle event matches (spec §5 stateEntered/transitionApplied).</summary>
    internal async Task<IReadOnlyList<EventStart>> LifecycleStartsAsync(Uow u, LifecycleEvent ev, CancellationToken ct)
    {
        var starts = new List<EventStart>();
        foreach (var t in (await u.S.ListTriggersAsync(ct)).Where(t => t.Lifecycle == ev.Lifecycle && t.Type is TriggerTypes.StateEntered or TriggerTypes.TransitionApplied))
        {
            if (ev.At < t.PublishedAt) continue;
            var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
            var trig = c.Definition.Trigger!;
            if (trig.Type == TriggerTypes.StateEntered && trig.States?.Contains(ev.ToState) != true) continue;
            if (trig.Type == TriggerTypes.TransitionApplied && (ev.Transition is null || trig.Transitions?.Contains(ev.Transition) != true)) continue;
            var info = new TriggerInfo
            {
                Type = trig.Type, Field = null, From = ev.FromState, To = ev.ToState, Transition = ev.Transition, ActorType = ev.ActorType, ActorId = ev.ActorId,
                ActorName = ev.ActorName, At = Values.FormatInstant(ev.At), EventId = ev.EventId, Comment = ev.Comment,
            };
            starts.Add(new EventStart(t.DefinitionId, t.Version, ev.Entity, ev.EntityId, ev.EventId, info, ev.Actor, ev.Depth, ev.CausationDefinitionId, null, ev.CorrelationId));
        }
        return starts;
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
            if (change.Kind == ChangeKinds.Updated && (t.Field is null || !change.Fields.ContainsKey(t.Field))) continue;
            var c = await CompiledAsync(u, t.DefinitionId, t.Version, ct);
            foreach (var old in (await u.S.FindDefinitionBookmarksAsync(c.DefinitionId, BookmarkKinds.DateReached, ct)).Where(b => b.EntityId == change.EntityId))
                await u.S.RemoveBookmarkAsync(old.Id, ct);
            if (change.Kind != ChangeKinds.Deleted) await PlanDateTriggerAsync(u, c, change.Entity, change.EntityId, ct);
        }
    }

    /// <summary>
    /// Plans a published dateReached trigger for every existing record (spec §5: "fires once per record when
    /// field + offset arrives"), replacing the bookmarks an earlier version planned.
    /// </summary>
    internal async Task PlanDateTriggersForAllAsync(Uow u, CompiledDefinition c, CancellationToken ct)
    {
        foreach (var old in await u.S.FindDefinitionBookmarksAsync(c.DefinitionId, BookmarkKinds.DateReached, ct)) await u.S.RemoveBookmarkAsync(old.Id, ct);
        if (c.Definition.Entity is not { } entity) return;
        await foreach (var id in AllIdsAsync(u, entity, ct)) await PlanDateTriggerAsync(u, c, entity, id, ct);
    }

    /// <summary>Adds the bookmark for one record's date (the caller removed any earlier one).</summary>
    private async Task PlanDateTriggerAsync(Uow u, CompiledDefinition c, string entity, string id, CancellationToken ct)
    {
        var trig = c.Definition.Trigger!;
        var snap = await u.S.Entities.GetAsync(entity, id, ct);
        var value = snap?[trig.Field!];
        var due = DateWaitDue(u.Calendar, value, trig.Offset, trig.At);
        // No date, a time already past, or one beyond the calendar's range (never): nothing to plan.
        if (due is null || due <= u.Now || due == DateTimeOffset.MaxValue) return;
        await u.S.AddBookmarkAsync(new BookmarkRecord
        {
            Id = NewId(), Kind = BookmarkKinds.DateReached, Purpose = BookmarkPurposes.DateReached, DefinitionId = c.DefinitionId, Entity = entity, EntityId = id, DueAt = due,
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
            // The claim covers the whole batch; once it lapses another worker may take the rest, so stop.
            if (b.LockedUntil is { } until && Now >= until) break;
            try
            {
                await RunAsync(u => FireAsync(u, b.Id, ct), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await RunAsync(u => TimerFailedAsync(u, b.Id, e is WorkflowException ? e.Message : $"{e.GetType().Name}: {e.Message}", ct), ct);
            }
        }
        return due.Count;
    }

    /// <summary>
    /// A timer could not be processed (its unit rolled back, so the bookmark is still there): count the attempt and
    /// back off 1, 2, 4… minutes up to an hour. After <see cref="MaxTimerAttempts"/> it is parked for an admin and the
    /// instance shows it: an automation or run token stops in an incident (retry, skip or cancel it as usual); a
    /// lifecycle record instance (automatic transition or SLA timer), or a task's reminder or escalation (the task stays
    /// open), gets the error on the instance and an <c>instance.incident</c> entry.
    /// </summary>
    private async Task TimerFailedAsync(Uow u, string bookmarkId, string error, CancellationToken ct)
    {
        var b = await u.S.GetBookmarkAsync(bookmarkId, ct);
        if (b is null) return;
        b.Attempts++;
        b.Error = error;
        var parked = b.Attempts >= MaxTimerAttempts;
        b.LockedUntil = parked ? DateTimeOffset.MaxValue : u.Now + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, b.Attempts - 1)));
        await u.S.UpdateBookmarkAsync(b, ct);
        await u.AuditAsync(new AuditEntry
        {
            Category = "instance", Action = "timerFailed", SubjectType = "bookmark", SubjectId = b.Id, InstanceId = b.InstanceId, DefinitionId = b.DefinitionId,
            Entity = b.Entity, EntityId = b.EntityId,
            Data = new Dictionary<string, object?> { ["purpose"] = b.Purpose, ["error"] = error, ["attempt"] = (double)b.Attempts, ["parked"] = parked },
        }, ct);
        if (!parked) return;
        Options.Logger.LogError("Timer {Bookmark} ({Purpose}) failed {Attempts} times and was parked: {Error}", b.Id, b.Purpose, b.Attempts, error);
        if (b.InstanceId is null) return;
        var message = $"A timer ({b.Purpose}) failed {b.Attempts} times and was stopped: {error}";
        var inst = await u.S.GetInstanceAsync(b.InstanceId, ct);
        if (inst is null || InstanceStatus.IsFinished(inst.Status)) return;
        if (inst.Kind == InstanceKinds.Record || b.Purpose is BookmarkPurposes.Reminder or BookmarkPurposes.Escalation)
        {
            var record = inst.Kind == InstanceKinds.Record
                ? u.Records.TryGetValue(inst.Id, out var held) ? held : u.Records[inst.Id] = inst
                : u.Execs.TryGetValue(inst.Id, out var exec) ? exec.I : inst;
            record.Error = message;
            record.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(record, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "instance", Action = "incident", SubjectType = "instance", SubjectId = record.Id, InstanceId = record.Id, DefinitionId = record.DefinitionId,
                Entity = record.Entity, EntityId = record.EntityId, CorrelationId = record.CorrelationId,
                Data = new Dictionary<string, object?> { ["bookmark"] = b.Id, ["purpose"] = b.Purpose, ["error"] = message },
            }, ct);
            return;
        }
        var x = await LoadExecAsync(u, inst.Id, ct);
        var t = b.TokenId is null ? null : x.S.Token(b.TokenId);
        var step = b.NodeId is null ? null : x.C.Step(b.NodeId);
        if (t is null || t.Status != TokenStatus.Waiting) return;
        // The token stops in an incident instead; its timers go (a retry runs the step again and plans new ones).
        await ClearWaitsAsync(x, t, ct);
        await u.S.RemoveBookmarkAsync(b.Id, ct);
        await RaiseIncidentAsync(x, t, step, message, ct);
        await SaveAsync(x, ct);
    }

    /// <summary>Fires one due bookmark (it is removed first; reminders and escalations leave the task open).</summary>
    internal async Task FireAsync(Uow u, string bookmarkId, CancellationToken ct)
    {
        var b = await u.S.GetBookmarkAsync(bookmarkId, ct);
        if (b is null) return;
        await u.S.RemoveBookmarkAsync(b.Id, ct);
        switch (b.Purpose)
        {
            case BookmarkPurposes.Schedule: await RunScheduleAsync(u, b, ct); return;
            case BookmarkPurposes.DateReached: await RunDateTriggerAsync(u, b, ct); return;
            case BookmarkPurposes.Reminder: await TaskReminderAsync(u, b, ct); return;
            case BookmarkPurposes.Escalation: await TaskEscalationAsync(u, b, ct); return;
            case BookmarkPurposes.Due: await TaskTimedOutAsync(u, b, ct); return;
        }
        if (b.InstanceId is null) return;
        var x = await LoadExecAsync(u, b.InstanceId, ct);
        if (InstanceStatus.IsFinished(x.I.Status)) return;
        // A lifecycle archived while the timer waited does nothing to its records (restoring plans the timers again).
        if (x.I.Kind == InstanceKinds.Record && (await u.S.GetDefinitionAsync(x.I.DefinitionId, ct))?.Status == DefinitionStatus.Archived) return;
        if (b.Purpose.StartsWith(BookmarkPurposes.AutoPrefix, StringComparison.Ordinal)) await FireAutoTransitionAsync(u, x, b.Purpose[BookmarkPurposes.AutoPrefix.Length..], ct);
        else if (b.Purpose.StartsWith(BookmarkPurposes.SlaPrefix, StringComparison.Ordinal)) await FireSlaAsync(u, x, b.Purpose[BookmarkPurposes.SlaPrefix.Length..], ct);
        else await FireTokenTimerAsync(x, b, ct);
    }

    /// <summary>An automatic transition's time came (spec §6 <c>auto.after</c>); its guard still applies.</summary>
    private async Task FireAutoTransitionAsync(Uow u, Exec x, string transitionId, CancellationToken ct)
    {
        var tr = x.C.TransitionById(transitionId);
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
    }

    /// <summary>A record stayed in a state longer than its SLA: audit it and run the SLA steps.</summary>
    private async Task FireSlaAsync(Uow u, Exec x, string stateId, CancellationToken ct)
    {
        var state = x.C.StateById(stateId);
        if (state is null || x.I.State != state.Id) return;
        await u.AuditAsync(new AuditEntry
        {
            Category = "lifecycle", Action = "slaBreached", SubjectType = "record", SubjectId = x.I.EntityId!, Entity = x.I.Entity, EntityId = x.I.EntityId,
            InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId, Data = new Dictionary<string, object?> { ["state"] = state.Key, ["within"] = state.Sla?.Within },
        }, ct);
        var trigger = new TriggerInfo { Type = "sla", Field = x.C.Lifecycle.Field, To = state.Key, At = Values.FormatInstant(u.Now) };
        var list = BookmarkPurposes.SlaPrefix + state.Id;
        await StartRunAsync(u, x.I, x.C, list, list, trigger, WorkflowActor.System, 0, null, ct);
    }

    /// <summary>A waiting token's timer: a wait ran out, a wait timed out, or a retry is due.</summary>
    private async Task FireTokenTimerAsync(Exec x, BookmarkRecord b, CancellationToken ct)
    {
        var t = b.TokenId is null ? null : x.S.Token(b.TokenId);
        if (t is null || t.Status != TokenStatus.Waiting) return;
        var step = b.NodeId is null ? null : x.C.Step(b.NodeId);
        if (step is null) return;
        switch (b.Purpose)
        {
            case BookmarkPurposes.Wait:
                x.S.SetOutput(step.Id, "timedOut", false);
                Resume(t);
                await CompleteWaitingStepAsync(x, t, step, ct);
                Advance(t);
                break;
            case BookmarkPurposes.Timeout:
                await ClearWaitsAsync(x, t, ct);
                x.S.SetOutput(step.Id, "timedOut", true);
                Resume(t);
                await CompleteWaitingStepAsync(x, t, step, ct);
                if (step is WaitStep { Until.OnTimeout: not null }) t.Frames.Add(new Frame { List = $"{step.Id}/onTimeout" });
                else Advance(t);
                break;
            case BookmarkPurposes.Retry:
                // The step runs again; its step.started entry carries the attempt number.
                Resume(t);
                break;
            default:
                return;
        }
        await DriveAsync(x, ct);
    }

    /// <summary>Adds the bookmark for a schedule's next run after <c>now</c> (none when the cron never fires again).</summary>
    internal static async Task PlanNextScheduleAsync(Uow u, string definitionId, Cron cron, CancellationToken ct)
    {
        if (cron.Next(u.Now, u.Calendar.Zone) is { } next)
            await u.S.AddBookmarkAsync(new BookmarkRecord { Id = NewId(), Kind = BookmarkKinds.Schedule, Purpose = BookmarkPurposes.Schedule, DefinitionId = definitionId, DueAt = next, CreatedAt = u.Now }, ct);
    }

    private async Task RunScheduleAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        var rec = b.DefinitionId is null ? null : await u.S.GetDefinitionAsync(b.DefinitionId, ct);
        if (rec is null || rec.Version == 0 || rec.Status == DefinitionStatus.Archived) return;
        var c = await CompiledAsync(u, rec.Id, rec.Version, ct);
        var trig = c.Definition.Trigger;
        if (trig?.Type != TriggerTypes.Schedule || !Cron.TryParse(trig.Cron, out var cron, out _)) return;
        // Plan the next run first, so a failure below doesn't stop the schedule.
        await PlanNextScheduleAsync(u, rec.Id, cron!, ct);
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
        await foreach (var id in AllIdsAsync(u, c.Definition.Entity, ct))
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

    /// <summary>Every id of an entity, read page by page.</summary>
    internal static async IAsyncEnumerable<string> AllIdsAsync(Uow u, string entity, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int page = 500;
        string? after = null;
        while (true)
        {
            var ids = await u.S.Entities.ListIdsAsync(entity, after, page, ct);
            foreach (var id in ids) yield return id;
            if (ids.Count < page) yield break;
            after = ids[^1];
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
        await TryStartFromEventAsync(u, c, new EventStart(c.DefinitionId, c.Version, b.Entity, b.EntityId, $"date:{b.Id}", info, WorkflowActor.System, 0, null, null, null), ct);
    }

    // ── Signals and manual starts ─────────────────────────────────────────

    /// <summary>
    /// The app raises a named event (spec §5 <c>signal</c>, §7 wait for signal), about one record or about
    /// none. A signal about a record resumes only that record's waits and starts automations of its entity;
    /// a signal about no record resumes only waits that are about no record and starts automations that
    /// have no entity. Returns how many waits it resumed plus how many instances it started.
    /// </summary>
    public Task<int> SignalAsync(string signal, string? entity, string? entityId, Dictionary<string, object?>? data, WorkflowActor? actor = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            if (Registry.Signal(signal) is null) throw new WorkflowException("unknownSignal", $"'{signal}' is not a registered signal.");
            if ((entity is null) != (entityId is null)) throw new WorkflowException("noRecord", "A signal is about a record (entity and id) or about none.");
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
                if (c.Definition.Entity != entity) continue;
                var info = new TriggerInfo
                {
                    Type = TriggerTypes.Signal, ActorType = actor?.Type ?? "system", ActorId = actor?.Id, ActorName = actor?.Name, At = Values.FormatInstant(u.Now),
                    EventId = eventId, Data = data,
                };
                var start = new EventStart(c.DefinitionId, c.Version, entity, entityId, eventId, info, actor ?? WorkflowActor.System, WorkflowWriteScope.Current?.Depth ?? 0,
                    WorkflowWriteScope.Current?.DefinitionId, null, null);
                if (await TryStartFromEventAsync(u, c, start, ct)) resumed++;
            }
            return resumed;
        }, ct);

    /// <summary>
    /// Whether a person may start a manual trigger: anyone signed in when it names no one, else its <c>who</c> (or a
    /// role the person carries). "The initiator" in <c>who</c> is the record's initiator (spec §8: whoever started
    /// its lifecycle), never the person asking; a record without one, or no record, has no initiator.
    /// </summary>
    internal async Task<bool> MayStartManualAsync(Uow u, Trigger trig, WorkflowActor user, string? entity, string? entityId, CancellationToken ct)
    {
        if (!user.IsUser) return false;
        if (trig.Who is not { Count: > 0 }) return true;
        if (user.Roles is { Count: > 0 } roles && trig.Who.Any(w => w.Role is not null && roles.Contains(w.Role))) return true;
        var initiator = entity is null || entityId is null ? null : await RecordInitiatorAsync(u, entity, entityId, ct);
        return (await ResolveAssigneesAsync(u.Scope(null, null, null, null, entity, entityId), trig.Who, null, initiator, ct)).Contains(user.Id!);
    }

    /// <summary>The record's initiator (spec §8): the person its oldest live lifecycle instance names, or null.</summary>
    internal static async Task<string?> RecordInitiatorAsync(Uow u, string entity, string id, CancellationToken ct) =>
        (await LiveRecordInstancesAsync(u, entity, id, ct)).Where(i => i.InitiatorId is not null).OrderBy(i => i.StartedAt).FirstOrDefault()?.InitiatorId;

    /// <summary>A person starts a manual-trigger automation from a record (spec §5 <c>manual</c>).</summary>
    public Task<InstanceView> StartManualAsync(string definitionKey, string? entity, string? entityId, Dictionary<string, object?>? input, WorkflowActor user, CancellationToken ct = default)
    {
        if (!user.IsUser) throw new WorkflowException("notAllowed", "Only a signed-in person can start a workflow by hand.");
        return RunAsync(async u =>
        {
            var rec = await u.S.GetDefinitionByKeyAsync(definitionKey, ct);
            if (rec is null || rec.Version == 0 || rec.Status == DefinitionStatus.Archived) throw new WorkflowException("unknownWorkflow", $"There is no published workflow '{definitionKey}'.");
            var c = await CompiledAsync(u, rec.Id, rec.Version, ct);
            var trig = c.Definition.Trigger;
            if (trig?.Type != TriggerTypes.Manual) throw new WorkflowException("notManual", $"'{c.Definition.Title}' cannot be started by hand.");
            if (c.Definition.Entity is not null && (entity != c.Definition.Entity || entityId is null)) throw new WorkflowException("noRecord", $"'{c.Definition.Title}' starts from a {c.Definition.Entity}.");
            if (!await MayStartManualAsync(u, trig, user, entity, entityId, ct)) throw new WorkflowException("notAllowed", $"You are not allowed to start '{c.Definition.Title}'.");
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
}
