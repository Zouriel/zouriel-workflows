using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Engine;

// SLA clock pause and resume (spec §6 sla.pauseOnStatus / pauseOnEvent / resumeOnEvent).
public sealed partial class WorkflowEngine
{
    private static bool UsesPause(Sla sla) => sla.PauseOnStatus is { Count: > 0 } || sla.PauseOnEvent is { Count: > 0 } || sla.ResumeOnEvent is { Count: > 0 };

    private static bool IsWorkingSla(Uow u, CompiledDefinition c, Sla sla) =>
        (sla.Calendar ?? c.Definition.Settings?.Calendar) == "working" && u.Calendar.HasWorkingTime;

    private AuditEntry SlaAudit(CompiledDefinition c, InstanceRecord record, string action, WorkflowActor? actor, Dictionary<string, object?> data) => new()
    {
        Category = "lifecycle", Action = action, SubjectType = "record", SubjectId = record.EntityId!, Entity = record.Entity, EntityId = record.EntityId,
        InstanceId = record.Id, DefinitionId = c.DefinitionId, Actor = actor ?? WorkflowActor.System, CorrelationId = record.CorrelationId, Data = data,
    };

    /// <summary>
    /// Before a record moves from <paramref name="from"/> to <paramref name="to"/> (the from-state's timers are still there): keeps the
    /// from-state's clock when <c>to</c> is one of its <c>pauseOnStatus</c>, carries kept clocks of other states that still apply, and
    /// drops the rest. Returns the audit entries to write once the move itself is audited. Does not save the instance.
    /// </summary>
    private async Task<List<AuditEntry>> PrepareSlaCarryAsync(Uow u, CompiledDefinition c, InstanceRecord record, State from, State to, WorkflowActor actor, CancellationToken ct)
    {
        var audits = new List<AuditEntry>();
        var clocks = SlaClocks.Read(record);
        var next = new Dictionary<string, SlaClock>(StringComparer.Ordinal);
        if (!to.IsTerminal && from.Id != to.Id && !from.IsTerminal && from.Sla is { } own && !clocks.ContainsKey(from.Id) && own.PauseOnStatus?.Contains(to.Id) == true)
        {
            var timer = (await u.S.ListBookmarksAsync(record.Id, ct)).FirstOrDefault(b => b.Purpose == BookmarkPurposes.SlaPrefix + from.Id);
            if (timer is null) next[from.Id] = new SlaClock { Ran = true, Reasons = [SlaClock.Status] };
            else
            {
                var working = IsWorkingSla(u, c, own);
                var left = u.Calendar.WorkingTimeBetween(u.Now, timer.DueAt ?? u.Now, working);
                next[from.Id] = new SlaClock { LeftMs = (long)left.TotalMilliseconds, Working = working, Reasons = [SlaClock.Status] };
                audits.Add(SlaAudit(c, record, "slaPaused", actor, new Dictionary<string, object?> { ["state"] = from.Key, ["reason"] = SlaClock.Status, ["status"] = to.Key }));
            }
        }
        foreach (var (id, k) in clocks)
        {
            var owner = c.StateById(id);
            if (owner?.Sla is null || to.IsTerminal || (to.Id == id && from.Id == id)) continue;
            if (to.Id == id) next[id] = k;
            else if (owner.Sla.PauseOnStatus?.Contains(to.Id) == true)
            {
                k.AddReason(SlaClock.Status);
                next[id] = k;
            }
        }
        if (next.Count > 0 || clocks.Count > 0) SlaClocks.Write(record, next);
        return audits;
    }

    /// <summary>
    /// Plans the SLA timer of a state the record is in, taking a kept clock into account. Returns true when a kept clock decided it
    /// (the caller plans nothing more); false when there is none (a fresh clock starts, as always).
    /// </summary>
    private async Task<bool> PlanKeptSlaAsync(Uow u, CompiledDefinition c, InstanceRecord record, State state, Sla sla, WorkflowActor? actor, string reason, CancellationToken ct)
    {
        if (record.Data.Length <= 2) return false;
        var clocks = SlaClocks.Read(record);
        if (!clocks.TryGetValue(state.Id, out var k)) return false;
        if (k.Ran) clocks.Remove(state.Id);
        else if (k.Due is { } due)
        {
            await AddSlaTimerAsync(u, record, state, due, ct);
            clocks.Remove(state.Id);
        }
        else
        {
            k.Reasons.Remove(SlaClock.Status);
            if (k.Reasons.Count == 0)
            {
                var at = u.Calendar.AddTime(u.Now, k.Left, k.Working);
                await AddSlaTimerAsync(u, record, state, at, ct);
                clocks.Remove(state.Id);
                await u.AuditAsync(SlaAudit(c, record, "slaResumed", actor, new Dictionary<string, object?>
                    { ["state"] = state.Key, ["reason"] = reason, ["due"] = Values.FormatInstant(at) }), ct);
            }
        }
        SlaClocks.Write(record, clocks);
        await u.S.UpdateInstanceAsync(record, ct);
        return true;
    }

    private async Task AddSlaTimerAsync(Uow u, InstanceRecord record, State state, DateTimeOffset due, CancellationToken ct) =>
        await u.S.AddBookmarkAsync(new BookmarkRecord
        {
            Id = NewId(), InstanceId = record.Id, Kind = BookmarkKinds.Timer, Purpose = BookmarkPurposes.SlaPrefix + state.Id, DueAt = due,
            Entity = record.Entity, EntityId = record.EntityId, DefinitionId = record.DefinitionId, CreatedAt = u.Now,
        }, ct);

    /// <summary>A signal about a record: pauses or resumes the clock of the state it is in (spec §6 sla.pauseOnEvent / resumeOnEvent).</summary>
    private async Task ApplySlaSignalAsync(Uow u, string signal, string entity, string entityId, WorkflowActor? actor, CancellationToken ct)
    {
        foreach (var record in await LiveRecordInstancesAsync(u, entity, entityId, ct))
        {
            if (record.Kind != InstanceKinds.Record || record.Status is not (InstanceStatus.Active or InstanceStatus.Incident) || record.State is null) continue;
            var def = await u.S.GetDefinitionAsync(record.DefinitionId, ct);
            if (def is null || def.Status == DefinitionStatus.Archived) continue;
            var c = await CompiledAsync(u, record.DefinitionId, record.Version, ct);
            if (c.StateById(record.State) is not { IsTerminal: false, Sla: { } sla } state) continue;
            var pauses = sla.PauseOnEvent?.Contains(signal) == true;
            var resumes = sla.ResumeOnEvent?.Contains(signal) == true;
            if (!pauses && !resumes) continue;
            var clocks = SlaClocks.Read(record);
            clocks.TryGetValue(state.Id, out var k);
            if (k is { Ran: true } or { Due: not null }) continue;
            if (resumes && k is not null && k.Reasons.Contains(SlaClock.Event))
            {
                k.Reasons.Remove(SlaClock.Event);
                if (k.Reasons.Count == 0)
                {
                    var at = u.Calendar.AddTime(u.Now, k.Left, k.Working);
                    await AddSlaTimerAsync(u, record, state, at, ct);
                    clocks.Remove(state.Id);
                    await u.AuditAsync(SlaAudit(c, record, "slaResumed", actor, new Dictionary<string, object?>
                        { ["state"] = state.Key, ["reason"] = SlaClock.Event, ["due"] = Values.FormatInstant(at) }), ct);
                }
            }
            else if (pauses && k is not null)
            {
                if (k.Reasons.Contains(SlaClock.Event)) continue;
                k.AddReason(SlaClock.Event);
            }
            else if (pauses)
            {
                var timer = (await u.S.ListBookmarksAsync(record.Id, ct)).FirstOrDefault(b => b.Purpose == BookmarkPurposes.SlaPrefix + state.Id);
                if (timer is null) continue;
                var working = IsWorkingSla(u, c, sla);
                var left = u.Calendar.WorkingTimeBetween(u.Now, timer.DueAt ?? u.Now, working);
                await u.S.RemoveBookmarkAsync(timer.Id, ct);
                clocks[state.Id] = new SlaClock { LeftMs = (long)left.TotalMilliseconds, Working = working, Reasons = [SlaClock.Event] };
                if (record.Status == InstanceStatus.Incident && IsParked(timer))
                {
                    MovedOutOfIncident(record);
                    record.Status = InstanceStatus.Active;
                }
                await u.AuditAsync(SlaAudit(c, record, "slaPaused", actor, new Dictionary<string, object?>
                    { ["state"] = state.Key, ["reason"] = SlaClock.Event, ["signal"] = signal }), ct);
            }
            else continue;
            SlaClocks.Write(record, clocks);
            record.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(record, ct);
        }
    }
}
