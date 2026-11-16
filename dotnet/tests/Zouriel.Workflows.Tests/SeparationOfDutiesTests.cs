using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;

namespace Zouriel.Workflows.Tests;

/// <summary>Maker-checker messages and paths the shared fixtures cannot check (error text, admin reassign, escalation), and the new registry lists.</summary>
public sealed class SeparationOfDutiesTests
{
    private static string Approval(string makerChecker, string extra = "") => $$"""
    { "specVersion": 1, "id": "wf_sod", "key": "sod", "kind": "automation", "title": "Sod", "entity": "application",
      "trigger": { "type": "manual", "label": "Go" },
      "steps": [ { "id": "ap", "type": "approval", "assignees": [ { "role": "reviewer" }, { "user": "fin1" } ], "rule": { "type": "any" }, "due": "P3D", "allowDelegation": true,
                   {{extra}} "makerChecker": {{makerChecker}}, "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
    """;

    private static async Task<(WorkflowTestHost H, string TaskId)> StartAsync(string makerChecker, string extra = "")
    {
        var h = await EngineBehaviourTests.HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        await h.PublishAsync(Approval(makerChecker, extra));
        await h.Engine.StartManualAsync("sod", "application", "a1", null, WorkflowActor.User("rev1", "Rana"));
        await h.SettleAsync();
        return (h, h.Store.All<TaskRecord>("tasks").Single().Id);
    }

    [Fact]
    public async Task The_initiator_is_refused_with_the_plain_message()
    {
        var (h, task) = await StartAsync("""{ "excludeInitiator": true }""");
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DecideAsync(task, WorkflowActor.User("rev1", "Rana"), "approved", null, null));
        Assert.Equal("segregationOfDuties", e.Code);
        Assert.Equal("You cannot approve this: the person who started it cannot approve it.", e.Message);
    }

    [Fact]
    public async Task A_person_without_the_required_role_is_told_which_role_it_needs()
    {
        var (h, task) = await StartAsync("""{ "requiredRole": "finance" }""");
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DelegateAsync(task, WorkflowActor.User("fin1", "Farah"), "ali", null));
        Assert.Equal("segregationOfDuties", e.Code);
        Assert.Equal("Ali Hassan cannot approve this: it needs the Finance role.", e.Message);
    }

    [Fact]
    public async Task An_admin_cannot_assign_a_refused_person_and_nothing_changes()
    {
        var (h, task) = await StartAsync("""{ "excludeInitiator": true }""");
        var before = h.Store.All<TaskRecord>("tasks").Single().Assignees;
        var e = await Assert.ThrowsAsync<WorkflowException>(() =>
            h.Engine.ReassignAsync(task, WorkflowActor.User("admin", "Admin", ["admin"]), ["rev2", "rev1"], "swap"));
        Assert.Equal("segregationOfDuties", e.Code);
        Assert.Equal(before, h.Store.All<TaskRecord>("tasks").Single().Assignees);
        // Someone allowed can be assigned.
        await h.Engine.ReassignAsync(task, WorkflowActor.User("admin", "Admin", ["admin"]), ["rev2"], "swap");
    }

    [Fact]
    public async Task Escalation_silently_skips_a_person_the_rule_refuses()
    {
        var (h, task) = await StartAsync("""{ "excludeInitiator": true }""", "\"escalation\": { \"after\": \"PT1H\", \"assignees\": [ { \"user\": \"rev1\" }, { \"user\": \"ali\" } ] },");
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        var t = h.Store.All<TaskRecord>("tasks").Single();
        Assert.Equal(1, t.EscalationLevel);
        Assert.DoesNotContain("rev1", System.Text.Json.JsonDocument.Parse(t.Assignees).RootElement.EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("ali", t.Assignees);
        Assert.Equal("segregationOfDuties", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DecideAsync(task, WorkflowActor.User("rev1", "Rana"), "approved", null, null))).Code);
    }

    [Fact]
    public async Task An_empty_makerChecker_is_no_rule()
    {
        var (h, task) = await StartAsync("{}");
        Assert.False(Definitions.Parse(Approval("{}")).Steps!.OfType<ApprovalStep>().Single().MakerChecker!.Any);
        await h.Engine.DecideAsync(task, WorkflowActor.User("rev1", "Rana"), "approved", null, null);
        Assert.DoesNotContain("excluded", h.Store.All<AuditRecord>("audit").Where(a => a.Action == "created" && a.Category == "task").Single().Data);
    }

    [Fact]
    public void The_registry_builder_serves_departments_portals_and_queues_only_when_there_are_any()
    {
        var plain = new RegistryBuilder().Build().ToJson();
        Assert.DoesNotContain("departments", plain);
        Assert.DoesNotContain("portals", plain);
        Assert.DoesNotContain("queues", plain);
        var r = new RegistryBuilder().Department("licensing", "Licensing department").Portal("staff", "Staff portal").Queue("frontDesk", "Front desk queue").Build();
        var back = WorkflowRegistry.FromJson(r.ToJson());
        Assert.Equal("Licensing department", back.Departments!.Single().Label);
        Assert.Equal("staff", back.Portals!.Single().Key);
        Assert.Equal("Front desk queue", back.Queues!.Single().Label);
    }

    [Fact]
    public void Assignees_and_public_processing_time_round_trip_without_extra_keys()
    {
        var def = Definitions.Parse("""
            { "specVersion": 1, "id": "w", "key": "w", "kind": "automation", "title": "W", "publicProcessingTime": { "estimate": "5 days", "display": true },
              "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "queue": "frontDesk" }, { "portal": "staff" }, { "department": "licensing" } ] } ] }
            """);
        var json = Definitions.Serialize(def);
        Assert.Contains("\"publicProcessingTime\":{\"estimate\":\"5 days\",\"display\":true}", json);
        Assert.Contains("{\"queue\":\"frontDesk\"}", json);
        Assert.DoesNotContain("makerChecker", json);
        Assert.DoesNotContain("publicProcessingTime", Definitions.Serialize(Definitions.Parse("{\"specVersion\":1,\"id\":\"w\",\"key\":\"w\",\"kind\":\"automation\",\"title\":\"W\"}")));
    }
}
