using System.Text.Json.Nodes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;
using Xunit.Abstractions;

namespace Zouriel.Workflows.Tests;

/// <summary>Regressions found in review: each test failed before its fix.</summary>
public sealed class EngineRegressionTests(ITestOutputHelper output)
{
    private static async Task<WorkflowTestHost> HostAsync()
    {
        var dir = SpecPaths.Fixtures("engine");
        var registry = WorkflowRegistry.FromJson(await File.ReadAllTextAsync(Path.Combine(dir, "_registry.json")));
        var host = new WorkflowTestHost(registry);
        foreach (var u in JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "_users.json")))!.AsArray())
            host.Identity.Add(new WorkflowUser(u!["id"]!.GetValue<string>(), u["name"]!.GetValue<string>(), u["email"]?.GetValue<string>(),
                (u["roles"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList(), null), u["manager"]?.GetValue<string>());
        return host;
    }

    private void Dump(WorkflowTestHost h)
    {
        foreach (var i in h.Store.All<InstanceRecord>("instances").OrderBy(i => i.StartedAt))
            output.WriteLine($"{i.DefinitionKey} {i.Kind} {i.Reason} {i.Status} {i.Error}");
    }

    [Fact]
    public async Task Queued_instances_keep_draining_when_a_dequeued_one_finishes_at_once()
    {
        var h = await HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_q", "key": "serial", "kind": "automation", "title": "Serial", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "b", "type": "branch", "paths": [ { "id": "p", "label": "checked", "when": { "ref": "trigger.to", "op": "eq", "value": { "const": "checked" } },
              "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] } ] } ] }
        """);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" }); await h.SettleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "done" }); await h.SettleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "new" }); await h.SettleAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Dump(h);
        Assert.DoesNotContain(h.Store.All<InstanceRecord>("instances"), i => i.Status == InstanceStatus.Queued);
    }

    private const string Lc = """
    { "specVersion": 1, "id": "lc_t", "key": "lct", "kind": "lifecycle", "title": "LC", "entity": "application",
      "lifecycle": { "field": "status", "mode": "enforce",
        "states": [
          { "id": "st_draft", "key": "draft", "label": "Draft", "category": "initial" },
          { "id": "st_sub", "key": "submitted", "label": "Submitted", "category": "active",
            "onEnter": [
              { "id": "at1", "type": "applyTransition", "lifecycle": "lct", "transition": "approve", "ifNotAllowed": "fail" },
              { "id": "n1", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["after leaving"] } ] },
          { "id": "st_ok", "key": "approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [
          { "id": "tr_submit", "key": "submit", "label": "Submit", "from": ["st_draft"], "to": "st_sub", "who": [ { "role": "reviewer" } ] },
          { "id": "tr_ok", "key": "approve", "label": "Approve", "from": ["st_sub"], "to": "st_ok", "hidden": true } ] } }
    """;

    [Fact]
    public async Task An_onEnter_run_that_moves_its_own_record_ends_exactly_once()
    {
        var h = await HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["status"] = "draft" });
        await h.SettleAsync();
        await h.PublishAsync(Lc);
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        await h.SettleAsync();
        Dump(h);
        var run = h.Store.All<InstanceRecord>("instances").Single(i => i.Reason == "onEnter:st_sub");
        var acts = h.Store.All<AuditRecord>("audit").Where(a => a.InstanceId == run.Id && a.Category == "instance").Select(a => a.Action).ToList();
        output.WriteLine(string.Join(",", acts));
        output.WriteLine("notifications sent: " + h.Notifications.Sent.Count);
        // A run must end exactly once.
        Assert.Equal(1, acts.Count(a => a is "cancelled" or "completed" or "failed"));
    }

    [Fact]
    public async Task Retrying_one_of_two_incidents_keeps_the_instance_in_incident()
    {
        var h = await HostAsync();
        h.Actions.Set("crm.ping", failTimes: 2);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_p", "key": "par", "kind": "automation", "title": "Par", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "p", "type": "parallel", "join": { "type": "all" }, "paths": [
             { "id": "a", "label": "A", "steps": [ { "id": "x1", "type": "action", "action": "crm.ping", "input": {} } ] },
             { "id": "b", "label": "B", "steps": [ { "id": "x2", "type": "action", "action": "crm.ping", "input": {} } ] } ] } ] }
        """);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" }); await h.SettleAsync();
        var inst = h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == "par");
        Assert.Equal(InstanceStatus.Incident, inst.Status);
        var after = await h.Engine.RetryAsync(inst.Id, WorkflowActor.User("admin", "Admin"));
        output.WriteLine($"after retry: {after.Status}; tokens: {string.Join(",", after.Tokens!.Select(t => t.Status + "/" + t.Waiting))}");
        Assert.Equal(InstanceStatus.Incident, after.Status);
    }

    [Fact]
    public async Task A_stateEntered_automation_does_not_retrigger_itself()
    {
        var h = await HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["status"] = "draft" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_l", "key": "loop", "kind": "lifecycle", "title": "Loop", "entity": "application",
          "lifecycle": { "field": "status", "mode": "enforce",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                        { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                             { "id": "t2", "key": "back", "label": "Back", "from": ["s"], "to": "d", "hidden": true },
                             { "id": "t3", "key": "resubmit", "label": "Resubmit", "from": ["d"], "to": "s", "hidden": true } ] } }
        """);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_e", "key": "onSub", "kind": "automation", "title": "On submitted", "entity": "application",
          "trigger": { "type": "stateEntered", "lifecycle": "loop", "states": ["submitted"] },
          "steps": [ { "id": "a1", "type": "applyTransition", "lifecycle": "loop", "transition": "back", "ifNotAllowed": "skip" },
                     { "id": "a2", "type": "applyTransition", "lifecycle": "loop", "transition": "resubmit", "ifNotAllowed": "skip" } ] }
        """);
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        await h.SettleAsync();
        var n = h.Store.All<InstanceRecord>("instances").Count(i => i.DefinitionKey == "onSub");
        output.WriteLine($"onSub instances: {n}; suppressed: {h.Store.All<AuditRecord>("audit").Count(a => a.Action == "suppressed")}");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Recordless_signal_starts_do_not_queue()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_n", "key": "nightly", "kind": "automation", "title": "Nightly",
          "trigger": { "type": "signal", "signal": "nightly" },
          "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }
        """);
        await h.Engine.SignalAsync("nightly", null, null, null);
        await h.Engine.SignalAsync("nightly", null, null, null);
        Dump(h);
        Assert.DoesNotContain(h.Store.All<InstanceRecord>("instances"), i => i.Status == InstanceStatus.Queued);
    }

    [Fact]
    public async Task DateReached_fires_for_records_that_existed_before_publishing()
    {
        var h = await HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["deadline"] = "2026-10-25" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_d", "key": "due", "kind": "automation", "title": "Due", "entity": "application",
          "trigger": { "type": "dateReached", "field": "deadline", "offset": "-P1D" },
          "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["due"] } ] }
        """);
        await h.AdvanceAsync(TimeSpan.FromDays(10));
        Assert.Contains(h.Store.All<InstanceRecord>("instances"), i => i.DefinitionKey == "due");
    }

    [Fact]
    public async Task Republishing_keeps_change_events_not_yet_processed()
    {
        var h = await HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        var json = """
        { "specVersion": 1, "id": "wf_r", "key": "rep", "kind": "automation", "title": "Rep", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["x"] } ] }
        """;
        await h.PublishAsync(json);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" }); // committed, relay not run yet
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.PublishAsync(json.Replace("\"Rep\"", "\"Rep v2\""));
        await h.SettleAsync();
        Assert.Contains(h.Store.All<InstanceRecord>("instances"), i => i.DefinitionKey == "rep");
    }
}
