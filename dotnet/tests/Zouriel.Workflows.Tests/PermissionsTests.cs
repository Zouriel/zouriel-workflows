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

    private static async Task<WorkflowTestHostWrapper> PublishedAsync()
    {
        var h = await EngineBehaviourTests.HostAsync();
        await h.PublishAsync(Review);
        return new WorkflowTestHostWrapper(h);
    }

    [Fact]
    public async Task A_permission_change_publishes_only_itself_and_keeps_the_draft_edits()
    {
        var w = await PublishedAsync();
        var e = w.H.Engine;
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
        Assert.Contains(w.H.Store.All<Storage.AuditRecord>("audit"), a => a.Category == "definition" && a.Action == "permissionsChanged");
    }

    [Fact]
    public async Task Unknown_nodes_and_unpublished_workflows_are_refused()
    {
        var w = await PublishedAsync();
        var bad = await Assert.ThrowsAsync<WorkflowException>(() => w.H.Engine.UpdatePermissionsAsync("lc_p", [("nope", Who("head"))], null, Admin));
        Assert.Equal("unknownNode", bad.Code);
        var trig = await Assert.ThrowsAsync<WorkflowException>(() => w.H.Engine.UpdatePermissionsAsync("lc_p", [("trigger", Who("head"))], null, Admin));
        Assert.Equal("unknownNode", trig.Code);
        await w.H.Engine.CreateDefinitionAsync(Review.Replace("lc_p", "lc_q").Replace("\"review\"", "\"review2\""), Admin);
        var draftOnly = await Assert.ThrowsAsync<WorkflowException>(() => w.H.Engine.UpdatePermissionsAsync("lc_q", [("t", Who("head"))], null, Admin));
        Assert.Equal("notPublished", draftOnly.Code);
    }

    public sealed record WorkflowTestHostWrapper(Testing.WorkflowTestHost H);
}
