using System.Text.Json.Nodes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;

namespace Zouriel.Workflows.Tests;

/// <summary>The reset list's undo beyond what the shared fixture checks: deleted records, app action undos, refusals.</summary>
public sealed class ResetUndoTests
{
    private static readonly WorkflowActor Admin = WorkflowActor.User("admin", "Admin", ["admin"]);
    private static readonly WorkflowActor Ali = WorkflowActor.User("ali", "Ali");

    private const string Review = """
    { "specVersion": 1, "id": "lc_r", "key": "review", "kind": "lifecycle", "title": "Review", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" },
        { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
          "steps": [
            { "id": "doc", "type": "createRecord", "entity": "document", "values": [ { "field": "verified", "value": { "const": true } } ] },
            { "id": "inv", "type": "action", "action": "finance.createInvoice", "input": { "amount": { "const": 100 } } },
            { "id": "mail", "type": "notify", "channel": "email", "to": [ { "field": "entity.applicant" } ], "body": "Thanks" } ] } ],
        "reset": { "who": [ { "role": "admin" } ] } } }
    """;

    private static async Task<Testing.WorkflowTestHost> SubmittedAsync(bool undoableInvoice)
    {
        var h = await EngineBehaviourTests.HostAsync();
        h.Actions.Set("finance.createInvoice", new Dictionary<string, object?> { ["invoiceNo"] = "INV-7" });
        if (undoableInvoice) h.Actions.Undoable("finance.createInvoice");
        await h.PublishAsync(Review);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
        await h.SettleAsync();
        return h;
    }

    private static int Documents(Testing.WorkflowTestHost h) => h.Store.PeekRecords("document").Count;

    [Fact]
    public async Task Undoing_deletes_what_the_workflow_created_and_calls_the_action_undo_with_its_output()
    {
        var h = await SubmittedAsync(undoableInvoice: true);
        Assert.Equal(1, Documents(h));
        var preview = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        Assert.Equal(["created", "action", "message"], preview.Items.Select(i => i.Kind));
        Assert.All(preview.Items.Take(2), i => Assert.True(i.Selected && i.Undoable));
        Assert.False(preview.Items[2].Undoable);

        await h.Engine.ResetAsync("application", "a1", null, Admin, undo: preview.Items.Where(i => i.Selected).Select(i => i.Id).ToList());
        await h.SettleAsync();

        Assert.Equal(0, Documents(h));
        var undo = Assert.Single(h.Actions.Undone);
        Assert.Equal("INV-7", undo.Output["invoiceNo"]);
        Assert.Equal("admin", undo.Actor.Id);
        // The undo's idempotency key is the step that ran the action, so a retried reset can tell it already undid it.
        Assert.Equal(preview.Items[1].Id, $"action:{undo.IdempotencyKey}");
    }

    [Fact]
    public async Task Unticked_items_stay_and_things_that_cannot_be_undone_are_refused()
    {
        var h = await SubmittedAsync(undoableInvoice: false);
        var preview = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        var action = preview.Items.Single(i => i.Kind == "action");
        Assert.False(action.Undoable);
        Assert.False(action.Selected);
        var message = preview.Items.Single(i => i.Kind == "message");
        var refused = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ResetAsync("application", "a1", null, Admin, undo: [message.Id]));
        Assert.Equal("notUndoable", refused.Code);

        // Nothing ticked: the reset still happens and the created document stays.
        await h.Engine.ResetAsync("application", "a1", null, Admin, undo: []);
        Assert.Equal(1, Documents(h));
        Assert.Equal("draft", h.Store.PeekRecord("application", "a1")!["status"]);
    }

    // ── Helpers for the tests below ───────────────────────────────────────

    private static ResetItem Item(ResetPreview p, string id) => Assert.Single(p.Items, i => i.Id == id);

    private static List<StepRecord> Journaled(WorkflowTestHost h) =>
        h.Store.All<StepRecord>("steps").Where(s => s.Undo is not null).ToList();

    private static Dictionary<string, object?> UndoOf(StepRecord s) => (Dictionary<string, object?>)Values.FromJson(s.Undo!)!;

    // ── Order (steps sharing a time) ──────────────────────────────────────

    private const string Ordered = """
    { "specVersion": 1, "id": "lc_o", "key": "ordered", "kind": "lifecycle", "title": "Ordered", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" },
        { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
          "steps": [ { "id": "hi", "type": "setFields", "values": [ { "field": "priority", "value": { "const": "high" } } ] } ] } ],
        "reset": { "who": [ { "role": "admin" } ] } } }
    """;

    // Two automations about the record, one after the other: the first reacts to the lifecycle's write, the second to the first's.
    private const string First = """
    { "specVersion": 1, "id": "wf_1", "key": "first", "kind": "automation", "title": "First", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["high"] },
      "steps": [
        { "id": "ping", "type": "action", "action": "crm.ping", "input": {} },
        { "id": "na", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "a" } }, { "field": "priority", "value": { "const": "low" } } ] } ] }
    """;

    private const string Second = """
    { "specVersion": 1, "id": "wf_2", "key": "second", "kind": "automation", "title": "Second", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["low"] },
      "steps": [
        { "id": "inv", "type": "action", "action": "finance.createInvoice", "input": { "amount": { "const": 100 } } },
        { "id": "nb", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "b" } } ] } ] }
    """;

    [Fact]
    public async Task Work_done_at_the_same_moment_keeps_its_order_first_value_before_and_newest_action_undone_first()
    {
        // The clock never moves, so every step of every run has the same start time; only seq orders them.
        // Run ids are random, so without seq the order is a coin toss: repeat to make a lucky pass very unlikely.
        for (var round = 0; round < 12; round++)
        {
            var h = await EngineBehaviourTests.HostAsync();
            h.Actions.Undoable("finance.createInvoice");
            h.Actions.Undoable("crm.ping");
            await h.PublishAsync(Ordered);
            await h.PublishAsync(First);
            await h.PublishAsync(Second);
            await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali", ["priority"] = "normal" });
            await h.SettleAsync();
            await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
            await h.SettleAsync();
            Assert.Equal("b", h.Store.PeekRecord("application", "a1")!["notes"]);

            var seqs = Journaled(h).Select(s => UndoOf(s)["seq"]).Cast<double>().ToList();
            Assert.Equal(seqs.Count, seqs.Distinct().Count());

            var preview = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
            Assert.EndsWith("back to nothing", Item(preview, "field:application:a1:notes").Title);
            Assert.EndsWith("back to Normal", Item(preview, "field:application:a1:priority").Title);
            Assert.Equal(["Undo “Ping CRM”", "Undo “Create invoice”"], preview.Items.Where(i => i.Kind == "action").Select(i => i.Title));

            await h.Engine.ResetAsync("application", "a1", null, Admin, undo: preview.Items.Where(i => i.Selected).Select(i => i.Id).ToList());
            await h.SettleAsync();
            Assert.Equal(["finance.createInvoice", "crm.ping"], h.Actions.Undone.Select(u => u.ActionKey));
            Assert.Null(h.Store.PeekRecord("application", "a1")!["notes"]);
        }
    }

    // ── Settling: what a reset considered is never listed again ───────────

    private const string Settle = """
    { "specVersion": 1, "id": "lc_s", "key": "settle", "kind": "lifecycle", "title": "Settle", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" },
        { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
          "steps": [ { "id": "hi", "type": "setFields", "values": [ { "field": "priority", "value": { "const": "high" } } ] } ] } ],
        "reset": { "who": [ { "role": "admin" } ], "cancelAutomations": false,
          "steps": [ { "id": "rn", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "Started over" } } ] } ] } } }
    """;

    private const string Slow = """
    { "specVersion": 1, "id": "wf_slow", "key": "slow", "kind": "automation", "title": "Slow", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["high"] },
      "steps": [
        { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "P1D" } },
        { "id": "sc", "type": "setFields", "values": [ { "field": "score", "value": { "const": 5 } } ] } ] }
    """;

    [Fact]
    public async Task A_second_reset_lists_only_what_happened_since_the_first_including_later_steps_of_runs_started_before_it()
    {
        // A clock that moves: the window must not depend on when things happened relative to the reset.
        var h = await EngineBehaviourTests.HostAsync();
        await h.PublishAsync(Settle);
        await h.PublishAsync(Slow);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali", ["priority"] = "normal" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
        await h.SettleAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        // First reset: nothing ticked; the automation keeps waiting (cancelAutomations: false).
        var first = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        Assert.Equal(["field:application:a1:priority"], first.Items.Select(i => i.Id));
        await h.Engine.ResetAsync("application", "a1", null, Admin, undo: []);
        await h.SettleAsync();
        Assert.All(Journaled(h).Where(s => s.NodeId == "hi"), s => Assert.True(UndoOf(s).ContainsKey("settled")));

        // Later: a person lowers the priority, the record is submitted again, and the waiting automation carries on.
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["priority"] = "low" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
        await h.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.Equal(5d, h.Store.PeekRecord("application", "a1")!["score"]);

        var second = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        // Priority goes back to what it was before this round (Low), not before the first; the reset's own note is not listed.
        Assert.EndsWith("back to Low", Item(second, "field:application:a1:priority").Title);
        Assert.Contains(second.Items, i => i.Id == "field:application:a1:score");
        Assert.DoesNotContain(second.Items, i => i.Id == "field:application:a1:notes");
    }

    // ── The token: a stale list never overwrites newer edits ──────────────

    [Fact]
    public async Task A_list_that_changed_since_it_was_opened_fails_the_reset_even_when_its_items_are_still_there()
    {
        var h = await EngineBehaviourTests.HostAsync();
        await h.PublishAsync(Settle);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali", ["priority"] = "normal" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
        await h.SettleAsync();

        var opened = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        Assert.Equal(opened.Token, (await h.Engine.GetResetPreviewAsync("application", "a1", Admin)).Token);
        // Someone edits the field after the list was opened: the item is still there, but its value is newer.
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["priority"] = "low" });
        await h.SettleAsync();
        var stale = await Assert.ThrowsAsync<WorkflowException>(() =>
            h.Engine.ResetAsync("application", "a1", null, Admin, undo: ["field:application:a1:priority"], token: opened.Token));
        Assert.Equal("resetChanged", stale.Code);
        Assert.Equal("low", h.Store.PeekRecord("application", "a1")!["priority"]);
        Assert.Equal("submitted", h.Store.PeekRecord("application", "a1")!["status"]);

        var fresh = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        Assert.NotEqual(opened.Token, fresh.Token);
        await h.Engine.ResetAsync("application", "a1", null, Admin, undo: ["field:application:a1:priority"], token: fresh.Token);
        Assert.Equal("normal", h.Store.PeekRecord("application", "a1")!["priority"]);
    }

    // ── Undo writes start no automations ──────────────────────────────────

    private const string OnNormal = """
    { "specVersion": 1, "id": "wf_n", "key": "onNormal", "kind": "automation", "title": "On normal", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "priority", "to": ["normal"] },
      "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "score", "value": { "const": 99 } } ] } ] }
    """;

    [Fact]
    public async Task Putting_a_field_back_starts_no_automation_that_could_redo_or_react_to_it()
    {
        var h = await EngineBehaviourTests.HostAsync();
        await h.PublishAsync(Settle);
        await h.PublishAsync(OnNormal);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali", ["priority"] = "normal" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
        await h.SettleAsync();

        // The reset's undo puts priority back to normal: the automation watching for that does not start.
        await h.Engine.ResetAsync("application", "a1", null, Admin, undo: ["field:application:a1:priority"]);
        await h.SettleAsync();
        Assert.Equal("normal", h.Store.PeekRecord("application", "a1")!["priority"]);
        Assert.Null(h.Store.PeekRecord("application", "a1")!["score"]);
        Assert.DoesNotContain(h.Store.All<InstanceRecord>("instances"), i => i.DefinitionKey == "onNormal");
        // The record's lifecycle still followed the change event (it was processed without error) ...
        Assert.All(h.Store.All<OutboxRecord>("outbox"), o => Assert.NotNull(o.ProcessedAt));

        // ... and the same write by a person does start it.
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["priority"] = "high" });
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["priority"] = "normal" });
        await h.SettleAsync();
        Assert.Equal(99d, h.Store.PeekRecord("application", "a1")!["score"]);
    }

    // ── Messages only when sent; records changed since they were created ──

    private const string Creates = """
    { "specVersion": 1, "id": "lc_c", "key": "creates", "kind": "lifecycle", "title": "Creates", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" },
        { "id": "b", "key": "submitted", "label": "Submitted", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "submit", "label": "Submit", "from": ["a"], "to": "b", "who": [ { "field": "entity.applicant" } ],
          "steps": [
            { "id": "doc", "type": "createRecord", "entity": "document", "values": [ { "field": "verified", "value": { "const": true } } ] },
            { "id": "nobody", "type": "notify", "channel": "email", "to": [ { "field": "entity.reviewer" } ], "body": "Nobody gets this" } ] } ],
        "reset": { "who": [ { "role": "admin" } ] } } }
    """;

    [Fact]
    public async Task A_record_people_changed_since_the_workflow_created_it_is_unticked_and_a_message_to_nobody_is_not_listed()
    {
        var h = await EngineBehaviourTests.HostAsync();
        await h.PublishAsync(Creates);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, Ali);
        await h.SettleAsync();
        var doc = Assert.Single(h.Store.PeekRecords("document"));

        var untouched = await h.Engine.GetResetPreviewAsync("application", "a1", Admin);
        Assert.Equal(["created"], untouched.Items.Select(i => i.Kind));
        Assert.True(untouched.Items[0].Selected);

        await h.Store.ChangeRecordAsync("document", doc.Id, new() { ["verified"] = false });
        await h.SettleAsync();
        var created = Item(await h.Engine.GetResetPreviewAsync("application", "a1", Admin), $"created:document:{doc.Id}");
        Assert.False(created.Selected);
        Assert.True(created.Undoable);
        Assert.Equal("Changed since the workflow created it; ticking it deletes it anyway.", created.Warning);
    }

    // ── Two lifecycles on one record, moves, webhooks, hidden values ──────

    private const string CaseRegistry = """
    { "specVersion": 1, "entities": [
      { "key": "case", "label": "Case", "titleField": "title", "fields": [
        { "key": "title", "label": "Title", "type": "text" },
        { "key": "status", "label": "Status", "type": "enum", "lifecycle": true, "settable": true, "watchable": true,
          "options": [ { "value": "open", "label": "Open" }, { "value": "done", "label": "Done" } ] },
        { "key": "phase", "label": "Phase", "type": "enum", "lifecycle": true, "watchable": true,
          "options": [ { "value": "one", "label": "One" }, { "value": "two", "label": "Two" } ] },
        { "key": "note", "label": "Note", "type": "text", "settable": true, "watchable": true },
        { "key": "score", "label": "Score", "type": "number", "settable": true },
        { "key": "secret", "label": "Secret", "type": "text", "settable": true, "sensitive": true },
        { "key": "task", "label": "Task", "type": "relation", "entity": "task" } ] },
      { "key": "task", "label": "Task", "titleField": "name", "fields": [
        { "key": "name", "label": "Name", "type": "text" },
        { "key": "status", "label": "Status", "type": "enum", "lifecycle": true,
          "options": [ { "value": "todo", "label": "To do" }, { "value": "doing", "label": "Doing" } ] } ] } ] }
    """;

    private const string CaseStatus = """
    { "specVersion": 1, "id": "lc_cs", "key": "caseStatus", "kind": "lifecycle", "title": "Case status", "entity": "case",
      "lifecycle": { "field": "status", "states": [
        { "id": "o", "key": "open", "label": "Open", "category": "initial" },
        { "id": "d", "key": "done", "label": "Done", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "finish", "label": "Finish", "from": ["o"], "to": "d", "who": [ { "role": "admin" } ],
          "steps": [
            { "id": "n", "type": "setFields", "values": [ { "field": "note", "value": { "const": "Finished" } } ] },
            { "id": "sec", "type": "setFields", "values": [ { "field": "secret", "value": { "const": "s3cret" } } ] },
            { "id": "mv", "type": "applyTransition", "target": "entity.task", "lifecycle": "taskStatus", "transition": "start", "ifNotAllowed": "skip" },
            { "id": "hook", "type": "webhook", "url": "https://hooks.example.com", "path": "/done", "method": "POST" } ] } ],
        "reset": { "label": "Reopen", "who": [ { "role": "admin" } ] } } }
    """;

    private const string CasePhase = """
    { "specVersion": 1, "id": "lc_cp", "key": "casePhase", "kind": "lifecycle", "title": "Case phase", "entity": "case",
      "lifecycle": { "field": "phase", "states": [
        { "id": "p1", "key": "one", "label": "One", "category": "initial" },
        { "id": "p2", "key": "two", "label": "Two", "category": "active" } ],
        "transitions": [ { "id": "t", "key": "advance", "label": "Advance", "from": ["p1"], "to": "p2", "who": [ { "role": "head" } ],
          "steps": [ { "id": "sc", "type": "setFields", "values": [ { "field": "score", "value": { "const": 7 } } ] } ] } ],
        "reset": { "label": "Back to phase one", "who": [ { "role": "head" } ] } } }
    """;

    private const string TaskStatus = """
    { "specVersion": 1, "id": "lc_ts", "key": "taskStatus", "kind": "lifecycle", "title": "Task status", "entity": "task",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "todo", "label": "To do", "category": "initial" },
        { "id": "b", "key": "doing", "label": "Doing", "category": "active" } ],
        "transitions": [ { "id": "start", "key": "start", "label": "Start", "from": ["a"], "to": "b" } ] } }
    """;

    private static readonly WorkflowActor Head = WorkflowActor.User("hd", "Hana", ["head"]);

    private static async Task<WorkflowTestHost> CaseHostAsync()
    {
        var h = new WorkflowTestHost(WorkflowRegistry.FromJson(CaseRegistry));
        h.Identity.Add(new WorkflowUser("admin", "Admin", null, ["admin"], null));
        h.Identity.Add(new WorkflowUser("hd", "Hana", null, ["head"], null));
        var (_, revision) = await h.Engine.GetSettingsAsync();
        await h.Engine.SaveSettingsAsync(GlobalSettings.Parse("""{ "version": 1, "webhooks": { "allowedOrigins": ["https://hooks.example.com"] } }"""), revision, Admin);
        await h.PublishAsync(TaskStatus);
        await h.PublishAsync(CaseStatus);
        await h.PublishAsync(CasePhase);
        await h.Store.CreateRecordAsync("task", "t1", new() { ["name"] = "Paperwork" });
        await h.Store.CreateRecordAsync("case", "c1", new() { ["title"] = "Flood", ["task"] = "t1" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("case", "c1", "finish", null, Admin, "caseStatus");
        await h.Engine.ApplyTransitionAsync("case", "c1", "advance", null, Head, "casePhase");
        await h.SettleAsync();
        return h;
    }

    [Fact]
    public async Task Each_lifecycle_of_a_record_lists_checks_and_offers_only_its_own_reset_and_work()
    {
        var h = await CaseHostAsync();
        var status = await h.Engine.GetResetPreviewAsync("case", "c1", Admin, "caseStatus");
        Assert.Equal("Reopen", status.Reset.Label);
        Assert.DoesNotContain(status.Items, i => i.Id == "field:case:c1:score");
        Assert.Contains(status.Items, i => i.Id == "field:case:c1:note");

        // The phase lifecycle's reset is for heads only: its own who decides, and its own option is offered.
        var phase = await h.Engine.GetResetPreviewAsync("case", "c1", Head, "casePhase");
        Assert.Equal("Back to phase one", phase.Reset.Label);
        Assert.Equal(["field:case:c1:score"], phase.Items.Select(i => i.Id));
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.GetResetPreviewAsync("case", "c1", Admin, "casePhase"))).Code);
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.GetResetPreviewAsync("case", "c1", Head, "caseStatus"))).Code);

        // Undoing the phase lifecycle's work leaves the status lifecycle's work alone.
        await h.Engine.ResetAsync("case", "c1", null, Head, "casePhase", undo: ["field:case:c1:score"]);
        Assert.Null(h.Store.PeekRecord("case", "c1")!["score"]);
        Assert.Equal("Finished", h.Store.PeekRecord("case", "c1")!["note"]);
        Assert.Contains((await h.Engine.GetResetPreviewAsync("case", "c1", Admin, "caseStatus")).Items, i => i.Id == "field:case:c1:note");
    }

    [Fact]
    public async Task Moves_of_other_records_and_webhooks_are_listed_but_not_undoable_and_hidden_values_stay_hidden()
    {
        var h = await CaseHostAsync();
        Assert.Equal("doing", h.Store.PeekRecord("task", "t1")!["status"]);
        var preview = await h.Engine.GetResetPreviewAsync("case", "c1", Admin, "caseStatus");
        Assert.Equal(["field", "field", "move", "webhook"], preview.Items.Select(i => i.Kind));

        var move = preview.Items.Single(i => i.Kind == "move");
        Assert.Equal("“Start” on Task “Paperwork”", move.Title);
        Assert.False(move.Undoable);
        Assert.False(move.Selected);
        Assert.Equal("Moves of other records aren't taken back.", move.Warning);
        var hook = preview.Items.Single(i => i.Kind == "webhook");
        Assert.Equal("Webhook POST https://hooks.example.com/done", hook.Title);
        Assert.False(hook.Undoable);
        Assert.Equal("notUndoable", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ResetAsync("case", "c1", null, Admin, "caseStatus", undo: [move.Id]))).Code);

        var secret = Item(preview, "field:case:c1:secret");
        Assert.Equal("Secret of Case “Flood”: back to (hidden)", secret.Title);
        Assert.Equal("Now (hidden)", secret.Detail);
        Assert.DoesNotContain("s3cret", System.Text.Json.JsonSerializer.Serialize(preview));
        // A hidden value that changes still changes the token, without the token showing it.
        await h.Store.ChangeRecordAsync("case", "c1", new() { ["secret"] = "other" });
        Assert.NotEqual(preview.Token, (await h.Engine.GetResetPreviewAsync("case", "c1", Admin, "caseStatus")).Token);
    }

    private const string MakesCases = """
    { "specVersion": 1, "id": "wf_mk", "key": "makeCase", "kind": "automation", "title": "Make a case", "entity": "task",
      "trigger": { "type": "manual", "label": "Make", "who": [ { "role": "admin" } ] },
      "steps": [ { "id": "mk", "type": "createRecord", "entity": "case", "values": [ { "field": "status", "value": { "const": "done" } } ] } ] }
    """;

    [Fact]
    public async Task Create_record_refuses_a_field_a_lifecycle_published_after_it_controls()
    {
        var h = new WorkflowTestHost(WorkflowRegistry.FromJson(CaseRegistry));
        h.Identity.Add(new WorkflowUser("admin", "Admin", null, ["admin"], null));
        await h.PublishAsync(MakesCases);   // fine when published: no lifecycle owns case.status yet
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_min", "key": "caseStatus", "kind": "lifecycle", "title": "Case status", "entity": "case",
          "lifecycle": { "field": "status", "states": [ { "id": "o", "key": "open", "label": "Open", "category": "initial" } ], "transitions": [] } }
        """);
        await h.Store.CreateRecordAsync("task", "t1", new() { ["name"] = "Paperwork" });
        await h.SettleAsync();
        var run = await h.Engine.StartManualAsync("makeCase", "task", "t1", null, Admin);
        await h.SettleAsync();
        var inst = h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == "makeCase");
        Assert.Equal(InstanceStatus.Incident, inst.Status);
        Assert.Contains("controlled by a lifecycle", inst.Error);
        Assert.Empty(h.Store.PeekRecords("case"));
        Assert.NotNull(run);
    }
}
