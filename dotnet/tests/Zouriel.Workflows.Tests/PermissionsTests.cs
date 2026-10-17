using System.Text.Json.Nodes;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.Tests;

/// <summary>Changing who may do steps publishes straight away, without publishing or losing draft edits.</summary>
public sealed class PermissionsTests
{
    private static readonly WorkflowActor Admin = WorkflowActor.User("admin", "Admin", ["admin"]);

    private const string Review = """
    { "specVersion": 1, "id": "lc_p", "key": "review", "kind": "lifecycle", "title": "Review", "entity": "application",
      "lifecycle": { "field": "status", "states": [
        { "id": "a", "key": "draft", "label": "Draft", "category": "initial" },
        { "id": "b", "key": "approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [ { "id": "t", "key": "approve", "label": "Approve", "from": ["a"], "to": "b", "who": [ { "role": "reviewer" } ] } ],
        "reset": { "who": [ { "role": "admin" } ] } } }
    """;

    private static JsonArray Who(string role) => [new JsonObject { ["role"] = role }];

    private static async Task<Testing.WorkflowTestHost> PublishedAsync()
    {
        var h = await EngineBehaviourTests.HostAsync();
        await h.PublishAsync(Review);
        return h;
    }

    [Fact]
    public async Task A_permission_change_publishes_only_itself_and_keeps_the_draft_edits()
    {
        var h = await PublishedAsync();
        var e = h.Engine;
        // An unpublished edit in the draft: a new title.
        var draft = (await e.GetDefinitionAsync("lc_p")).Draft.AsObject();
        draft["title"] = "Review (new wording)";
        await e.SaveDraftAsync("lc_p", draft.ToJsonString(), Admin);

        var summary = await e.UpdatePermissionsAsync("lc_p", [("t", Who("finance")), ("reset", Who("reviewer"))], null, Admin);

        var detail = await e.GetDefinitionAsync("lc_p");
        Assert.Equal(2, summary.Version);
        var pub = detail.Published!;
        Assert.Equal("Review", pub["title"]!.GetValue<string>());
        Assert.Equal("finance", pub["lifecycle"]!["transitions"]![0]!["who"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("reviewer", pub["lifecycle"]!["reset"]!["who"]![0]!["role"]!.GetValue<string>());
        // The draft keeps its edit and gets the same permission changes.
        Assert.Equal("Review (new wording)", detail.Draft["title"]!.GetValue<string>());
        Assert.Equal("finance", detail.Draft["lifecycle"]!["transitions"]![0]!["who"]![0]!["role"]!.GetValue<string>());
        Assert.True(detail.Summary.HasDraftChanges);
        Assert.Contains(h.Store.All<Storage.AuditRecord>("audit"), a => a.Category == "definition" && a.Action == "permissionsChanged");
    }

    [Fact]
    public async Task Unknown_nodes_and_unpublished_workflows_are_refused()
    {
        var h = await PublishedAsync();
        var bad = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.UpdatePermissionsAsync("lc_p", [("nope", Who("head"))], null, Admin));
        Assert.Equal("unknownNode", bad.Code);
        var trig = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.UpdatePermissionsAsync("lc_p", [("trigger", Who("head"))], null, Admin));
        Assert.Equal("unknownNode", trig.Code);
        await h.Engine.CreateDefinitionAsync(Review.Replace("lc_p", "lc_q").Replace("\"review\"", "\"review2\""), Admin);
        var draftOnly = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.UpdatePermissionsAsync("lc_q", [("t", Who("head"))], null, Admin));
        Assert.Equal("notPublished", draftOnly.Code);
    }

    [Fact]
    public async Task Nodes_the_draft_no_longer_has_are_reported_and_audited()
    {
        var h = await PublishedAsync();
        var e = h.Engine;
        // The draft drops the reset; the published version still has it.
        var draft = (await e.GetDefinitionAsync("lc_p")).Draft.AsObject();
        draft["lifecycle"]!.AsObject().Remove("reset");
        await e.SaveDraftAsync("lc_p", draft.ToJsonString(), Admin);

        var summary = await e.UpdatePermissionsAsync("lc_p", [("t", Who("finance")), ("reset", Who("reviewer"))], null, Admin);

        Assert.True(summary.Changed);
        Assert.Equal(["reset"], summary.DraftSkipped);
        var detail = await e.GetDefinitionAsync("lc_p");
        Assert.Equal("reviewer", detail.Published!["lifecycle"]!["reset"]!["who"]![0]!["role"]!.GetValue<string>());
        Assert.Null(detail.Draft["lifecycle"]!["reset"]);
        var audit = h.Store.All<Storage.AuditRecord>("audit").Single(a => a.Action == "permissionsChanged");
        Assert.Contains("\"draftSkipped\":[\"reset\"]", audit.Data);
    }
}
