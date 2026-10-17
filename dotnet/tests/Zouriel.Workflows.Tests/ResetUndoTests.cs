using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Storage;

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
}
