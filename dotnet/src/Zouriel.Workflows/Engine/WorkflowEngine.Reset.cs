using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Engine;

/// <summary>One thing a workflow did that the reset list shows (spec §6 reset).</summary>
/// <param name="Id">Stable id to send back in the reset's <c>undo</c> list.</param>
/// <param name="Kind"><c>field</c> (a field it changed), <c>created</c> (a record it created), <c>action</c>, <c>message</c> or <c>webhook</c>.</param>
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
public sealed record ResetPreview(ResetOption Reset, IReadOnlyList<ResetItem> Items);

public sealed partial class WorkflowEngine
{
    /// <summary>One undoable thing, with what taking it back needs.</summary>
    private sealed record PlannedUndo(ResetItem Item, string Entity, string Id, string? Field, object? Before, string? Action, Dictionary<string, object?>? Input,
        Dictionary<string, object?>? Output, IUndoableWorkflowAction? Handler);

    /// <summary>
    /// What resetting a record would do (spec §6 reset): the reset option and every change its workflows made
    /// since it last started over (fields on any record, records created, actions, messages, webhooks). Only for
    /// people the reset allows.
    /// </summary>
    public Task<ResetPreview> GetResetPreviewAsync(string entity, string id, WorkflowActor viewer, string? lifecycle = null, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var view = await RecordViewAsync(u, entity, id, viewer, ct);
            if (view.Reset is null) throw new WorkflowException("notAllowed", "You cannot reset this record's workflow now.");
            var c = await LifecycleOfAsync(u, entity, id, lifecycle, ct);
            var record = await FindRecordInstanceAsync(u, c.DefinitionId, entity, id, ct);
            var plan = record is null ? [] : await PlanUndoAsync(u, c, record, ct);
            return new ResetPreview(view.Reset, plan.Select(p => p.Item).ToList());
        }, ct);

    /// <summary>What the record's workflows did since it last started over, merged per field and checked against now.</summary>
    private async Task<List<PlannedUndo>> PlanUndoAsync(Uow u, CompiledDefinition c, InstanceRecord record, CancellationToken ct)
    {
        var entity = record.Entity!;
        var id = record.EntityId!;
        var lastReset = (await u.S.QueryAuditAsync(new AuditQuery { Category = "lifecycle", Action = "reset", InstanceId = record.Id, Take = 1000 }, ct))
            .OrderByDescending(a => a.Seq).FirstOrDefault()?.At;
        var since = lastReset ?? DateTimeOffset.MinValue;
        var runs = (await u.S.QueryInstancesAsync(new InstanceQuery { Entity = entity, EntityId = id, Kinds = [InstanceKinds.Run, InstanceKinds.Automation], Take = 1000 }, ct))
            .Where(i => i.StartedAt >= since).ToList();
        var done = new List<(StepRecord Step, InstanceRecord Run, Dictionary<string, object?> Undo)>();
        foreach (var run in runs)
            foreach (var s in await u.S.ListStepsAsync(run.Id, ct))
                if (s.Status == StepStatus.Completed && s.Undo is not null && s.StartedAt >= since && Values.FromJson(s.Undo) is Dictionary<string, object?> undo)
                    done.Add((s, run, undo));
        done.Sort((a, b) => a.Step.StartedAt.CompareTo(b.Step.StartedAt));

        async Task<(string Workflow, string Step)> NamesAsync(InstanceRecord run, StepRecord s)
        {
            var rc = await CompiledAsync(u, run.DefinitionId, run.Version, ct);
            var step = rc.Step(s.NodeId);
            return (rc.Definition.Title, step?.Title ?? Lint.Linter.StepLabels.GetValueOrDefault(s.StepType) ?? s.StepType);
        }
        async Task<string> RecordNameAsync(string e, string rid)
        {
            var desc = Registry.Entity(e);
            var snap = await u.S.Entities.GetAsync(e, rid, ct);
            var title = desc?.TitleField is { } tf && snap is not null ? Values.Text(snap[tf]) : null;
            return $"{desc?.Label ?? e} “{(string.IsNullOrEmpty(title) ? rid : title)}”";
        }
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
        var created = done.Where(d => d.Undo.GetValueOrDefault("kind") as string == "created")
            .Select(d => (Entity: d.Undo["entity"] as string ?? "", Id: Values.Text(d.Undo["id"]))).ToHashSet();

        // Records the workflows created: deleting them takes back their creation and everything set on them since.
        foreach (var (s, run, undo) in done.Where(d => d.Undo.GetValueOrDefault("kind") as string == "created"))
        {
            var e = undo["entity"] as string ?? "";
            var rid = Values.Text(undo["id"]);
            if (await u.S.Entities.GetAsync(e, rid, ct) is null) continue;
            var (wf, step) = await NamesAsync(run, s);
            plan.Add(new PlannedUndo(new ResetItem($"created:{e}:{rid}", "created", $"Delete {await RecordNameAsync(e, rid)}", "Created by the workflow", wf, step, true, true, null),
                e, rid, null, null, null, null, null, null));
        }

        // Fields: one item per field of each record, back to its value before the first change.
        foreach (var g in done.Where(d => d.Undo.GetValueOrDefault("kind") as string == "fields")
                     .SelectMany(d => ((d.Undo.GetValueOrDefault("after") as Dictionary<string, object?>) ?? []).Keys.Select(f => (d, Field: f)))
                     .GroupBy(x => (Entity: x.d.Undo["entity"] as string ?? "", Id: Values.Text(x.d.Undo["id"]), x.Field)))
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
            var (wf, step) = await NamesAsync(last.d.Run, last.d.Step);
            var label = Registry.Field(e, field)?.Label ?? field;
            plan.Add(new PlannedUndo(
                new ResetItem($"field:{e}:{rid}:{field}", "field", $"{label} of {await RecordNameAsync(e, rid)}: back to {await ShowAsync(e, field, before)}",
                    $"Now {await ShowAsync(e, field, now)}", wf, step, true, !changedSince,
                    changedSince ? $"Changed again since the workflow set it to {await ShowAsync(e, field, after)}; ticking it overwrites that." : null),
                e, rid, field, before, null, null, null, null));
        }

        // Actions, messages and webhooks, in the order they happened.
        foreach (var (s, run, undo) in done)
        {
            var kind = undo.GetValueOrDefault("kind") as string;
            if (kind is not ("action" or "message" or "webhook")) continue;
            var (wf, step) = await NamesAsync(run, s);
            if (kind == "action")
            {
                var key = undo.GetValueOrDefault("action") as string ?? "";
                var desc = Registry.Action(key);
                var handler = desc is null || u.Simulation ? null : Options.Actions.Resolve(desc) as IUndoableWorkflowAction;
                plan.Add(new PlannedUndo(
                    new ResetItem($"action:{s.Id}", "action", $"Undo “{desc?.Label ?? key}”", null, wf, step, handler is not null, handler is not null,
                        handler is null ? "The app has no way to undo this action." : null),
                    entity, id, null, null, key, undo.GetValueOrDefault("input") as Dictionary<string, object?> ?? [], undo.GetValueOrDefault("output") as Dictionary<string, object?> ?? [], handler));
            }
            else
            {
                var title = kind == "message"
                    ? $"Message{(undo.GetValueOrDefault("subject") is string subject && subject.Length > 0 ? $" “{subject}”" : "")} to {string.Join(", ", (undo.GetValueOrDefault("to") as List<object?> ?? []).Select(Values.Text))}"
                    : $"Webhook {undo.GetValueOrDefault("method")} {undo.GetValueOrDefault("url")}";
                plan.Add(new PlannedUndo(new ResetItem($"{kind}:{s.Id}", kind, title, null, wf, step, false, false,
                    kind == "message" ? "Already sent; it can't be taken back." : "Already called; it can't be taken back."), entity, id, null, null, null, null, null, null));
            }
        }
        return plan;
    }

    /// <summary>A stored value as people read it (an option's label for enums).</summary>
    private string OptionText(string entity, string field, object? value)
    {
        var f = Registry.Field(entity, field);
        var text = Values.Text(value);
        return f?.Options?.FirstOrDefault(o => Values.Text(o.Value) == text)?.Label ?? text;
    }

    /// <summary>Takes back the chosen items of a reset (spec §6 reset), inside the reset's unit of work; returns their titles.</summary>
    private async Task<List<object?>> UndoChosenAsync(Uow u, CompiledDefinition c, InstanceRecord record, IReadOnlyCollection<string> chosen, WorkflowActor user, CancellationToken ct)
    {
        if (chosen.Count == 0) return [];
        var plan = await PlanUndoAsync(u, c, record, ct);
        var byId = plan.ToDictionary(p => p.Item.Id);
        foreach (var idChosen in chosen)
        {
            if (!byId.TryGetValue(idChosen, out var p))
                throw new WorkflowException("resetChanged", "What the workflow did has changed since this list was opened. Open it again.");
            if (!p.Item.Undoable) throw new WorkflowException("notUndoable", $"“{p.Item.Title}” can't be undone.");
        }
        var picked = plan.Where(p => chosen.Contains(p.Item.Id)).ToList();
        using var scope = WorkflowWriteScope.Begin(user, 1, record.Id, c.DefinitionId, record.CorrelationId);
        // App actions first (newest first), then fields back to their old values, then created records deleted.
        foreach (var p in picked.Where(p => p.Item.Kind == "action").Reverse())
            await p.Handler!.UndoAsync(new WorkflowActionUndoContext
            {
                ActionKey = p.Action!, Input = p.Input!, Output = p.Output!, Entity = record.Entity, EntityId = record.EntityId, Actor = user,
            }, ct);
        foreach (var g in picked.Where(p => p.Item.Kind == "field").GroupBy(p => (p.Entity, p.Id)))
            await u.S.Entities.SetFieldsAsync(g.Key.Entity, g.Key.Id, g.ToDictionary(p => p.Field!, p => p.Before), ct);
        foreach (var p in picked.Where(p => p.Item.Kind == "created"))
            await u.S.Entities.DeleteAsync(p.Entity, p.Id, ct);
        return picked.Select(p => (object?)p.Item.Title).ToList();
    }
}
