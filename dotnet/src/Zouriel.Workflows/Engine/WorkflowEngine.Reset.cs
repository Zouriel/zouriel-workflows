using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Engine;

/// <summary>One thing a workflow did that the reset list shows (spec §6 reset).</summary>
/// <param name="Id">Stable id to send back in the reset's <c>undo</c> list.</param>
/// <param name="Kind"><c>field</c> (a field it changed), <c>created</c> (a record it created), <c>action</c>, <c>message</c>, <c>webhook</c> or <c>move</c> (another record moved by <c>applyTransition</c>, never undoable).</param>
/// <param name="Title">What it was, in words ("Reviewer of Application “Bursary”: back to nobody").</param>
/// <param name="Detail">More about it (the value now, what created it), if any.</param>
/// <param name="Workflow">The workflow that did it.</param>
/// <param name="Step">The step that did it.</param>
/// <param name="Undoable">Whether the reset can take it back (messages, webhooks and actions without an undo cannot).</param>
/// <param name="Selected">Whether it is ticked by default (not when it was changed again since, or cannot be undone).</param>
/// <param name="Warning">Why it is unticked or cannot be undone, if so.</param>
public sealed record ResetItem(string Id, string Kind, string Title, string? Detail, string Workflow, string Step, bool Undoable, bool Selected, string? Warning);

/// <summary>What a reset would do: its option (label, target, form) and the workflow's work it can take back.</summary>
/// <param name="Reset">The reset as offered to the viewer.</param>
/// <param name="Items">What the record's workflows did since it last started over, oldest first.</param>
/// <param name="Token">
/// Fingerprint of the items and the values they would restore. Send it back with the reset: when the list has
/// changed since (someone edited a field it would restore), the reset fails with <c>resetChanged</c>.
/// </param>
public sealed record ResetPreview(ResetOption Reset, IReadOnlyList<ResetItem> Items, string Token);

public sealed partial class WorkflowEngine
{
    /// <summary>One item of the reset list, with what taking it back needs.</summary>
    /// <param name="Item">The item as listed.</param>
    /// <param name="Entity">The record it is about (fields, created records).</param>
    /// <param name="Id">That record's id.</param>
    /// <param name="Field">For fields: which one.</param>
    /// <param name="Before">For fields: the value to restore.</param>
    /// <param name="StepId">For actions: the step record that ran it (the undo's idempotency key).</param>
    /// <param name="Undo">For actions: the journal entry (action, input, output).</param>
    /// <param name="Handler">For actions: the handler that can undo it, if any.</param>
    private sealed record PlannedUndo(ResetItem Item, string Entity, string Id, string? Field = null, object? Before = null, string? StepId = null,
        Dictionary<string, object?>? Undo = null, IUndoableWorkflowAction? Handler = null);

    /// <summary>The reset list, the journal entries it was built from (settled by a reset) and its token.</summary>
    private sealed record UndoPlan(List<PlannedUndo> Items, List<(StepRecord Step, Dictionary<string, object?> Undo)> Considered, string Token);

    /// <summary>One journal entry of what a workflow did, with the run that did it.</summary>
    private sealed record JournalEntry(StepRecord Step, InstanceRecord Run, Dictionary<string, object?> Undo)
    {
        public string? Kind => Undo.GetValueOrDefault("kind") as string;
        public string Entity => Undo.GetValueOrDefault("entity") as string ?? "";
        public string Id => Values.Text(Undo.GetValueOrDefault("id"));
        public long Seq => SeqOf(Step, Undo);
    }

    private long _lastJournalSeq;

    /// <summary>
    /// Serializes an undo journal entry (spec §6) with its <c>seq</c>: the clock in milliseconds × 1000, raised past the
    /// last one handed out, so entries written in one unit of work (which all share a start time) keep their order.
    /// </summary>
    internal string Journal(Dictionary<string, object?> entry)
    {
        var floor = Now.ToUnixTimeMilliseconds() * 1000;
        long last, next;
        do
        {
            last = Interlocked.Read(ref _lastJournalSeq);
            next = Math.Max(floor, last + 1);
        } while (Interlocked.CompareExchange(ref _lastJournalSeq, next, last) != last);
        entry["seq"] = (double)next;
        return Values.Serialize(entry);
    }

    /// <summary>An entry's order: its <c>seq</c>, or for entries written before seq existed, its step's start.</summary>
    private static long SeqOf(StepRecord s, Dictionary<string, object?> undo) =>
        undo.GetValueOrDefault("seq") is double d ? (long)d : s.StartedAt.ToUnixTimeMilliseconds() * 1000;

    /// <summary>
    /// What resetting a record would do (spec §6 reset): the reset option of the requested lifecycle and every change
    /// its workflows made since it last started over (fields on any record, records created, actions, messages,
    /// webhooks, moves of other records). Only for people that lifecycle's reset allows.
    /// </summary>
    public Task<ResetPreview> GetResetPreviewAsync(string entity, string id, WorkflowActor viewer, string? lifecycle = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            if (Registry.Entity(entity) is null) throw new WorkflowException("unknownEntity", $"'{entity}' is not a workflow record type.");
            var snap = await u.S.Entities.GetAsync(entity, id, ct) ?? throw new WorkflowException("noRecord", "That record does not exist.");
            var c = await LifecycleOfAsync(u, entity, id, lifecycle, ct);
            var record = await FindRecordInstanceAsync(u, c.DefinitionId, entity, id, ct);
            var shown = record ?? TransientRecordInstance(c, entity, id, snap, u.Now);
            var rc = shown.Version == c.Version ? c : await CompiledAsync(u, shown.DefinitionId, shown.Version, ct);
            var option = await OfferedResetAsync(u, rc, shown, viewer, ct)
                         ?? throw new WorkflowException("notAllowed", "You cannot reset this record's workflow now.");
            var plan = record is null ? new UndoPlan([], [], TokenOf([])) : await PlanUndoAsync(u, rc, record, ct);
            return new ResetPreview(option, plan.Items.Select(p => p.Item).ToList(), plan.Token);
        }, ct);

    /// <summary>
    /// The reset a lifecycle offers the viewer for a record (the record view and the reset list share it): null when
    /// the record's state is not among its <c>states</c>, the viewer is not among its <c>who</c>, or it would change nothing.
    /// </summary>
    internal async Task<ResetOption?> OfferedResetAsync(Uow u, CompiledDefinition rc, InstanceRecord record, WorkflowActor viewer, CancellationToken ct)
    {
        if (rc.Lifecycle.Reset is not { } r || !ResetOfferedIn(r, record) || !await MayResetAsync(u, rc, record, viewer, ct)) return null;
        var back = ResetTarget(rc);
        // Not offered when it would change nothing (already back, nothing running).
        var changes = record.State != back.Id || record.Status == InstanceStatus.OutOfSync || (await RunningAboutAsync(u, record, r, ct)).Count > 0;
        return changes ? new ResetOption(r.Label ?? "Reset workflow", back.Key, back.Label, r.Confirm, r.Input?.Form.DeepClone(), r.ResolvedComment) : null;
    }

    /// <summary>
    /// The journal entries a reset of this record instance considers (spec §6): those of its own runs (not the reset
    /// runs) and of the automations about the record, not yet settled by an earlier reset, in the order they happened.
    /// Runs of another lifecycle's record instance are never included.
    /// </summary>
    private static async Task<List<JournalEntry>> JournalAsync(Uow u, InstanceRecord record, CancellationToken ct)
    {
        var runs = (await u.S.QueryInstancesAsync(new InstanceQuery { ParentId = record.Id, Kinds = [InstanceKinds.Run], Take = 1000 }, ct)).ToList();
        runs.AddRange(await u.S.QueryInstancesAsync(new InstanceQuery { Entity = record.Entity, EntityId = record.EntityId, Kinds = [InstanceKinds.Automation], Take = 1000 }, ct));
        var entries = new List<JournalEntry>();
        foreach (var run in runs.Where(r => r.Reason != "reset").DistinctBy(r => r.Id))
            foreach (var s in await u.S.ListStepsAsync(run.Id, ct))
                if (s.Status == StepStatus.Completed && s.Undo is not null && Values.FromJson(s.Undo) is Dictionary<string, object?> undo && !undo.ContainsKey("settled"))
                    entries.Add(new JournalEntry(s, run, undo));
        // A stable sort: entries keep their seq order even when steps share a start time.
        return entries.OrderBy(e => e.Seq).ToList();
    }

    /// <summary>What the record's workflows did since it last started over, merged per field and checked against now.</summary>
    private async Task<UndoPlan> PlanUndoAsync(Uow u, CompiledDefinition c, InstanceRecord record, CancellationToken ct)
    {
        var entity = record.Entity!;
        var id = record.EntityId!;
        var done = await JournalAsync(u, record, ct);

        async Task<(string Workflow, string Step)> NamesAsync(JournalEntry d)
        {
            var rc = await CompiledAsync(u, d.Run.DefinitionId, d.Run.Version, ct);
            var step = rc.Step(d.Step.NodeId);
            return (rc.Definition.Title, step?.Title ?? Lint.Linter.StepLabels.GetValueOrDefault(d.Step.StepType) ?? d.Step.StepType);
        }
        async Task<string> RecordNameAsync(string e, string rid)
        {
            var desc = Registry.Entity(e);
            var snap = await u.S.Entities.GetAsync(e, rid, ct);
            var title = desc?.TitleField is { } tf && snap is not null ? Values.Text(snap[tf]) : null;
            return $"{desc?.Label ?? e} “{(string.IsNullOrEmpty(title) ? rid : title)}”";
        }
        bool Sensitive(string e, string field) => Registry.Field(e, field)?.Sensitive == true;
        // Values as people read them: option labels, people's names, related records' titles.
        async Task<string> ShowAsync(string e, string field, object? v)
        {
            var f = Registry.Field(e, field);
            if (f?.Sensitive == true) return "(hidden)";
            if (Values.IsEmpty(v)) return "nothing";
            if (f?.Type == FieldTypes.User) return (await Options.Identity.GetUserAsync(Values.Text(v), ct))?.Name ?? Values.Text(v);
            if (f?.Type == FieldTypes.Relation && f.Entity is not null) return await RecordNameAsync(f.Entity, Values.Text(v));
            return OptionText(e, field, v);
        }

        var plan = new List<PlannedUndo>();
        // Per item, what the token covers beyond the item itself: the raw values it compares (never a hidden value).
        var prints = new List<object?>();
        var created = done.Where(d => d.Kind == "created").Select(d => (d.Entity, d.Id)).ToHashSet();

        // Records the workflows created: deleting them takes back their creation and everything set on them since.
        foreach (var d in done.Where(d => d.Kind == "created"))
        {
            var (e, rid) = (d.Entity, d.Id);
            var snap = await u.S.Entities.GetAsync(e, rid, ct);
            if (snap is null) continue;
            // What the record should hold if only the workflows touched it: its first values, then their later writes.
            var expected = d.Undo.GetValueOrDefault("values") is Dictionary<string, object?> first ? new Dictionary<string, object?>(first) : null;
            if (expected is not null)
                foreach (var w in done.Where(w => w.Kind == "fields" && w.Entity == e && w.Id == rid && w.Seq > d.Seq))
                    foreach (var (k, v) in (w.Undo.GetValueOrDefault("after") as Dictionary<string, object?>) ?? [])
                        expected[k] = v;
            var changedSince = expected?.Any(kv => snap.Values.ContainsKey(kv.Key) && !Values.DeepEquals(Values.Normalize(snap[kv.Key]), Values.Normalize(kv.Value))) == true;
            var (wf, step) = await NamesAsync(d);
            plan.Add(new PlannedUndo(new ResetItem($"created:{e}:{rid}", "created", $"Delete {await RecordNameAsync(e, rid)}", "Created by the workflow", wf, step, true,
                !changedSince, changedSince ? "Changed since the workflow created it; ticking it deletes it anyway." : null), e, rid));
            prints.Add(new Dictionary<string, object?>
            {
                ["changed"] = changedSince,
                ["values"] = snap.Values.Where(kv => !Sensitive(e, kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
            });
        }

        // Fields: one item per field of each record, back to its value before the first change.
        foreach (var g in done.Where(d => d.Kind == "fields")
                     .SelectMany(d => ((d.Undo.GetValueOrDefault("after") as Dictionary<string, object?>) ?? []).Keys.Select(f => (d, Field: f)))
                     .GroupBy(x => (x.d.Entity, x.d.Id, x.Field)))
        {
            var (e, rid, field) = g.Key;
            // The lifecycle's own field is moved by the reset itself; a record about to be deleted needs nothing.
            if ((e == entity && rid == id && field == c.Lifecycle.Field) || created.Contains((e, rid))) continue;
            var snap = await u.S.Entities.GetAsync(e, rid, ct);
            if (snap is null) continue;
            var first = g.First();
            var last = g.Last();
            var before = Values.Normalize((first.d.Undo["before"] as Dictionary<string, object?>)?.GetValueOrDefault(field));
            var after = Values.Normalize((last.d.Undo["after"] as Dictionary<string, object?>)?.GetValueOrDefault(field));
            var now = Values.Normalize(snap[field]);
            if (Values.DeepEquals(now, before)) continue;
            var changedSince = !Values.DeepEquals(now, after);
            var (wf, step) = await NamesAsync(last.d);
            var label = Registry.Field(e, field)?.Label ?? field;
            plan.Add(new PlannedUndo(
                new ResetItem($"field:{e}:{rid}:{field}", "field", $"{label} of {await RecordNameAsync(e, rid)}: back to {await ShowAsync(e, field, before)}",
                    $"Now {await ShowAsync(e, field, now)}", wf, step, true, !changedSince,
                    changedSince ? $"Changed again since the workflow set it to {await ShowAsync(e, field, after)}; ticking it overwrites that." : null),
                e, rid, field, before));
            // A hidden value only says what the item already shows (it differs from before, and whether it is still the workflow's).
            prints.Add(Sensitive(e, field) ? new Dictionary<string, object?> { ["changed"] = changedSince } : new Dictionary<string, object?> { ["now"] = now });
        }

        // Actions, messages, webhooks and moves of other records, in the order they happened.
        foreach (var d in done)
        {
            var kind = d.Kind;
            if (kind is not ("action" or "message" or "webhook" or "move")) continue;
            var (wf, step) = await NamesAsync(d);
            var undo = d.Undo;
            if (kind == "action")
            {
                var key = undo.GetValueOrDefault("action") as string ?? "";
                var desc = Registry.Action(key);
                var handler = desc is null || u.Simulation ? null : Options.Actions.Resolve(desc) as IUndoableWorkflowAction;
                plan.Add(new PlannedUndo(
                    new ResetItem($"action:{d.Step.Id}", "action", $"Undo “{desc?.Label ?? key}”", null, wf, step, handler is not null, handler is not null,
                        handler is null ? "The app has no way to undo this action." : null),
                    entity, id, StepId: d.Step.Id, Undo: undo, Handler: handler));
            }
            else if (kind == "move")
            {
                var (e, rid) = (d.Entity, d.Id);
                var lcKey = undo.GetValueOrDefault("lifecycle") as string ?? "";
                // This record's own lifecycle is moved by the reset itself.
                if (e == entity && rid == id && lcKey == c.Definition.Key) continue;
                var transitionKey = undo.GetValueOrDefault("transition") as string ?? "";
                var label = transitionKey;
                if (await u.S.GetDefinitionByKeyAsync(lcKey, ct) is { Version: > 0 } lcDef)
                    label = (await CompiledAsync(u, lcDef.Id, lcDef.Version, ct)).Lifecycle?.Transitions.FirstOrDefault(t => t.Key == transitionKey)?.Label ?? transitionKey;
                plan.Add(new PlannedUndo(new ResetItem($"move:{d.Step.Id}", "move", $"“{label}” on {await RecordNameAsync(e, rid)}", null, wf, step, false, false,
                    "Moves of other records aren't taken back."), e, rid));
            }
            else
            {
                var title = kind == "message"
                    ? $"Message{(undo.GetValueOrDefault("subject") is string subject && subject.Length > 0 ? $" “{subject}”" : "")} to {string.Join(", ", (undo.GetValueOrDefault("to") as List<object?> ?? []).Select(Values.Text))}"
                    : $"Webhook {undo.GetValueOrDefault("method")} {undo.GetValueOrDefault("url")}";
                plan.Add(new PlannedUndo(new ResetItem($"{kind}:{d.Step.Id}", kind, title, null, wf, step, false, false,
                    kind == "message" ? "Already sent; it can't be taken back." : "Already called; it can't be taken back."), entity, id));
            }
            prints.Add(null);
        }
        var token = TokenOf(plan.Select((p, i) => (object?)new Dictionary<string, object?>
        {
            ["item"] = Values.ToJson(new Dictionary<string, object?>
            {
                ["id"] = p.Item.Id, ["title"] = p.Item.Title, ["detail"] = p.Item.Detail, ["undoable"] = p.Item.Undoable, ["selected"] = p.Item.Selected, ["warning"] = p.Item.Warning,
            })?.ToJsonString(),
            ["values"] = prints[i],
        }).ToList());
        return new UndoPlan(plan, done.Select(d => (d.Step, d.Undo)).ToList(), token);
    }

    /// <summary>The reset list's token: a hash of its items and the values they compare.</summary>
    private static string TokenOf(List<object?> items) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Values.Serialize(items))))[..32].ToLowerInvariant();

    /// <summary>A stored value as people read it (an option's label for enums).</summary>
    private string OptionText(string entity, string field, object? value)
    {
        var f = Registry.Field(entity, field);
        var text = Values.Text(value);
        return f?.Options?.FirstOrDefault(o => Values.Text(o.Value) == text)?.Label ?? text;
    }

    /// <summary>
    /// Takes back the chosen items of a reset (spec §6 reset), inside the reset's unit of work; returns their titles.
    /// Every journal entry the list was built from is settled, so the next reset only lists what happens after this one.
    /// </summary>
    private async Task<List<object?>> UndoChosenAsync(Uow u, CompiledDefinition c, InstanceRecord record, IReadOnlyCollection<string> chosen, string? token,
        WorkflowActor user, CancellationToken ct)
    {
        var plan = await PlanUndoAsync(u, c, record, ct);
        var changed = new WorkflowException("resetChanged", "What the workflow did has changed since this list was opened. Open it again.");
        if (token is not null && token != plan.Token) throw changed;
        var byId = plan.Items.ToDictionary(p => p.Item.Id);
        foreach (var idChosen in chosen)
        {
            if (!byId.TryGetValue(idChosen, out var p)) throw changed;
            if (!p.Item.Undoable) throw new WorkflowException("notUndoable", $"“{p.Item.Title}” can't be undone.");
        }
        // Settled, ticked or not: unticked items stay as they are and are not offered again.
        var settledBy = NewId();
        foreach (var (step, undo) in plan.Considered)
        {
            undo["settled"] = settledBy;
            step.Undo = Values.Serialize(undo);
            await u.S.UpdateStepAsync(step, ct);
        }
        var picked = plan.Items.Where(p => chosen.Contains(p.Item.Id)).ToList();
        if (picked.Count == 0) return [];
        // Taking things back starts no automations, so none can redo what was undone.
        using var scope = WorkflowWriteScope.Begin(user, 1, record.Id, c.DefinitionId, record.CorrelationId, suppressTriggers: true);
        // App actions first (newest first), then fields back to their old values, then created records deleted.
        foreach (var p in picked.Where(p => p.Item.Kind == "action").Reverse())
        {
            var undo = p.Undo!;
            await p.Handler!.UndoAsync(new WorkflowActionUndoContext
            {
                ActionKey = undo.GetValueOrDefault("action") as string ?? "",
                Input = undo.GetValueOrDefault("input") as Dictionary<string, object?> ?? [],
                Output = undo.GetValueOrDefault("output") as Dictionary<string, object?> ?? [],
                Entity = record.Entity, EntityId = record.EntityId, Actor = user, IdempotencyKey = p.StepId!,
            }, ct);
        }
        foreach (var g in picked.Where(p => p.Item.Kind == "field").GroupBy(p => (p.Entity, p.Id)))
            await u.S.Entities.SetFieldsAsync(g.Key.Entity, g.Key.Id, g.ToDictionary(p => p.Field!, p => p.Before), ct);
        foreach (var p in picked.Where(p => p.Item.Kind == "created"))
            await u.S.Entities.DeleteAsync(p.Entity, p.Id, ct);
        return picked.Select(p => (object?)p.Item.Title).ToList();
    }
}
