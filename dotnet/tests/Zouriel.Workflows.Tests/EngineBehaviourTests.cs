using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Tests;

/// <summary>Engine behaviours that the shared fixtures do not reach (admin paths, limits, masking, concurrency).</summary>
public sealed class EngineBehaviourTests
{
    private static readonly WorkflowActor Admin = WorkflowActor.User("admin", "Admin", ["admin"]);

    internal static async Task<WorkflowTestHost> HostAsync(Func<WorkflowEngineOptions, WorkflowEngineOptions>? configure = null)
    {
        var dir = SpecPaths.Fixtures("engine");
        var registry = WorkflowRegistry.FromJson(await File.ReadAllTextAsync(Path.Combine(dir, "_registry.json")));
        var host = new WorkflowTestHost(registry, configure: configure);
        foreach (var u in JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "_users.json")))!.AsArray())
            host.Identity.Add(new WorkflowUser(u!["id"]!.GetValue<string>(), u["name"]!.GetValue<string>(), u["email"]?.GetValue<string>(),
                (u["roles"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList(), null), u["manager"]?.GetValue<string>());
        return host;
    }

    private static async Task<WorkflowTestHost> WithRecordAsync(Dictionary<string, object?>? values = null)
    {
        var h = await HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", values ?? new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        return h;
    }

    private static async Task SettingsAsync(WorkflowTestHost h, string json)
    {
        var (_, revision) = await h.Engine.GetSettingsAsync();
        await h.Engine.SaveSettingsAsync(GlobalSettings.Parse(json), revision, Admin);
    }

    private static List<AuditRecord> Audit(WorkflowTestHost h, string category, string action) =>
        h.Store.All<AuditRecord>("audit").Where(a => a.Category == category && a.Action == action).OrderBy(a => a.Seq).ToList();

    private static InstanceRecord Instance(WorkflowTestHost h, string key) => h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == key);

    // ── Incidents ─────────────────────────────────────────────────────────

    private const string TwoFailingPaths = """
    { "specVersion": 1, "id": "wf_p", "key": "par", "kind": "automation", "title": "Par", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "p", "type": "parallel", "join": { "type": "all" }, "paths": [
         { "id": "a", "label": "A", "steps": [ { "id": "x1", "type": "action", "action": "crm.ping", "input": {} } ] },
         { "id": "b", "label": "B", "steps": [ { "id": "x2", "type": "action", "action": "crm.ping", "input": {} } ] } ] } ] }
    """;

    [Fact]
    public async Task Skip_acts_on_the_chosen_incident_and_shows_the_other()
    {
        var h = await WithRecordAsync();
        h.Actions.Set("crm.ping", failTimes: 100, failMessage: "CRM down");
        await h.PublishAsync(TwoFailingPaths);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        var id = Instance(h, "par").Id;
        var view = await h.Engine.SkipAsync(id, new() { ["ok"] = true }, Admin, "x2");
        Assert.Equal(InstanceStatus.Incident, view.Status);
        Assert.Equal("x1", view.ErrorStepId);
        Assert.Equal(true, ((Dictionary<string, object?>)view.Outputs!.GetType().GetProperty("Item")!.GetValue(view.Outputs, ["x2"])!)["ok"]);
        view = await h.Engine.SkipAsync(id, null, Admin);
        Assert.Equal(InstanceStatus.Completed, view.Status);
    }

    [Fact]
    public async Task Retry_with_an_unknown_target_is_refused()
    {
        var h = await WithRecordAsync();
        h.Actions.Set("crm.ping", failTimes: 100);
        await h.PublishAsync(TwoFailingPaths);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.RetryAsync(Instance(h, "par").Id, Admin, "nope"));
        Assert.Equal("notInIncident", e.Code);
    }

    [Fact]
    public async Task A_step_timeout_fails_the_attempt_instead_of_aborting_the_unit()
    {
        var h = await WithRecordAsync();
        h.Actions.Set("crm.ping", delay: TimeSpan.FromSeconds(30));
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_t", "key": "slow", "kind": "automation", "title": "Slow", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "x", "type": "action", "action": "crm.ping", "input": {}, "timeout": "PT1S" } ] }
        """);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        var inst = Instance(h, "slow");
        Assert.Equal(InstanceStatus.Incident, inst.Status);
        Assert.Contains("took longer", inst.Error);
    }

    [Fact]
    public async Task Each_instance_gets_its_own_step_budget()
    {
        var h = await HostAsync(o => new WorkflowEngineOptions
        {
            Registry = o.Registry, Store = o.Store, Identity = o.Identity, Notifications = o.Notifications, Webhooks = o.Webhooks, Actions = o.Actions,
            Time = o.Time, MaxStepsPerRun = 4,
        });
        string Def(string id) => $$"""
        { "specVersion": 1, "id": "{{id}}", "key": "{{id}}", "kind": "automation", "title": "{{id}}",
          "variables": [ { "id": "v", "key": "v", "type": "number" } ],
          "trigger": { "type": "signal", "signal": "nightly" },
          "steps": [ { "id": "s1", "type": "setVariables", "values": [ { "variable": "v", "value": { "const": 1 } } ] },
                     { "id": "s2", "type": "setVariables", "values": [ { "variable": "v", "value": { "const": 2 } } ] },
                     { "id": "s3", "type": "setVariables", "values": [ { "variable": "v", "value": { "const": 3 } } ] } ] }
        """;
        await h.PublishAsync(Def("one"));
        await h.PublishAsync(Def("two"));
        await h.Engine.SignalAsync("nightly", null, null, null);
        Assert.All(h.Store.All<InstanceRecord>("instances"), i => Assert.Equal(InstanceStatus.Completed, i.Status));
    }

    // ── Starts ────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_failing_start_does_not_stop_the_others()
    {
        var h = await WithRecordAsync();
        h.Actions.Set("crm.ping", failTimes: 100);
        h.Identity.Broken["broken"] = true;
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_bad", "key": "bad", "kind": "automation", "title": "Bad", "entity": "application",
          "settings": { "notifyOwnersOnIncident": true, "owners": [ { "user": "broken" } ] },
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "x", "type": "action", "action": "crm.ping", "input": {} } ] }
        """);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_good", "key": "good", "kind": "automation", "title": "Good", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": "changed" } ] }
        """);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        Assert.Equal(InstanceStatus.Completed, Instance(h, "good").Status);
        var failed = Assert.Single(Audit(h, "trigger", "failed"));
        Assert.Equal("wf_bad", failed.DefinitionId);
    }

    // ── Definitions ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_rollback_that_fails_lint_changes_nothing()
    {
        var h = await WithRecordAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_c", "key": "child", "kind": "automation", "title": "Child", "entity": "application",
          "trigger": { "type": "manual", "label": "Go" }, "steps": [] }
        """);
        const string v1 = """
        { "specVersion": 1, "id": "wf_r", "key": "parent", "kind": "automation", "title": "Parent", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "s", "type": "subWorkflow", "workflow": "child", "wait": false } ] }
        """;
        await h.PublishAsync(v1);
        await h.PublishAsync(v1.Replace("""[ { "id": "s", "type": "subWorkflow", "workflow": "child", "wait": false } ]""", "[]"));
        await h.Engine.ArchiveAsync("wf_c", Admin);
        var draftBefore = (await h.Engine.GetDefinitionAsync("wf_r")).Draft.ToJsonString();
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.RollbackAsync("wf_r", 1, Admin));
        Assert.Equal("lintErrors", e.Code);
        var after = await h.Engine.GetDefinitionAsync("wf_r");
        Assert.Equal(draftBefore, after.Draft.ToJsonString());
        Assert.Equal(2, after.Summary.Version);
        Assert.Empty(Audit(h, "definition", "rolledBack"));
    }

    [Fact]
    public async Task Migration_is_refused_when_a_waiting_step_moved()
    {
        var h = await WithRecordAsync();
        const string v1 = """
        { "specVersion": 1, "id": "wf_m", "key": "mig", "kind": "automation", "title": "Mig", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } },
                     { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": "x" } ] }
        """;
        await h.PublishAsync(v1);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_m", "key": "mig", "kind": "automation", "title": "Mig", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": "x" },
                     { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }
        """);
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.MigrateAsync(Instance(h, "mig").Id, 2, null, Admin));
        Assert.Equal("cannotMigrate", e.Code);
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    private const string AllApproval = """
    { "specVersion": 1, "id": "wf_a", "key": "appr", "kind": "automation", "title": "Appr", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "t", "type": "approval", "assignees": [ { "user": "rev1" }, { "user": "rev2" } ], "rule": { "type": "all" }, "due": "P3D",
                   "allowDelegation": true, "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
    """;

    private static async Task<(WorkflowTestHost H, string TaskId)> OpenApprovalAsync()
    {
        var h = await WithRecordAsync();
        await h.PublishAsync(AllApproval);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        return (h, h.Store.All<TaskRecord>("tasks").Single().Id);
    }

    [Fact]
    public async Task A_task_cannot_be_delegated_to_someone_who_already_decided()
    {
        var (h, task) = await OpenApprovalAsync();
        await h.Engine.DecideAsync(task, WorkflowActor.User("rev2", "Rafiq"), "approved", null, null);
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DelegateAsync(task, WorkflowActor.User("rev1", "Rana"), "rev2", null));
        Assert.Equal("alreadyAssigned", e.Code);
    }

    [Fact]
    public async Task Reassigning_to_people_who_all_decided_settles_the_task()
    {
        var (h, task) = await OpenApprovalAsync();
        await h.Engine.DecideAsync(task, WorkflowActor.User("rev1", "Rana"), "approved", null, null);
        var view = await h.Engine.ReassignAsync(task, Admin, ["rev1"], "rev2 left");
        Assert.Equal(Storage.TaskStatus.Completed, view.Status);
        Assert.Equal("approved", view.Outcome);
        await h.SettleAsync();
        Assert.Equal(InstanceStatus.Completed, Instance(h, "appr").Status);
    }

    [Fact]
    public async Task Reassigning_to_an_unknown_person_is_refused()
    {
        var (h, task) = await OpenApprovalAsync();
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ReassignAsync(task, Admin, ["ghost"], null));
        Assert.Equal("unknownUser", e.Code);
    }

    private const string RemindedApproval = """
    { "specVersion": 1, "id": "wf_r", "key": "rem", "kind": "automation", "title": "Rem", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage" },
      "steps": [ { "id": "t", "type": "approval", "title": "Check it", "assignees": [ { "user": "rev1" } ], "rule": { "type": "any" }, "due": "P3D",
                   "reminders": ["P1D"], "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
    """;

    [Theory]
    [InlineData(null, "22 Oct 2026, 09:00 UTC")]
    [InlineData("Asia/Riyadh", "22 Oct 2026, 12:00 Asia/Riyadh")]
    public async Task A_reminder_shows_the_due_time_readably_in_the_settings_time_zone(string? zone, string expected)
    {
        var h = await WithRecordAsync();
        if (zone is not null) await SettingsAsync(h, $$"""{ "version": 1, "timeZone": "{{zone}}" }""");
        await h.PublishAsync(RemindedApproval);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        await h.AdvanceAsync(TimeSpan.FromDays(2.5));
        var reminder = h.Notifications.Sent.Single(n => n.Subject?.StartsWith("Reminder: Check it", StringComparison.Ordinal) == true);
        Assert.Equal($"“Check it” is still waiting for you. It is due {expected}.", reminder.Body);
        // Machine fields stay ISO 8601.
        Assert.Equal("2026-10-22T09:00:00.000Z", Values.FormatInstant(h.Store.All<TaskRecord>("tasks").Single().DueAt));
    }

    [Fact]
    public async Task Decision_answers_have_a_size_limit()
    {
        var (h, task) = await OpenApprovalAsync();
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DecideAsync(task, WorkflowActor.User("rev1", "Rana"), "approved", null,
            new() { ["blob"] = new string('x', WorkflowEngine.MaxDecisionDataBytes + 1) }));
        Assert.Equal("tooLarge", e.Code);
    }

    // ── People only ───────────────────────────────────────────────────────

    private const string Lifecycle = """
    { "specVersion": 1, "id": "lc_p", "key": "lcp", "kind": "lifecycle", "title": "LC", "entity": "application",
      "lifecycle": { "field": "status", "mode": "enforce",
        "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                    { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
        "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                         { "id": "t2", "key": "approve", "label": "Approve", "from": ["s"], "to": "o", "who": [ { "role": "reviewer" } ], "hidden": true },
                         { "id": "t3", "key": "expire", "label": "Expire", "from": ["d"], "to": "o" } ] } }
    """;

    [Fact]
    public async Task Only_a_person_can_apply_a_transition_and_never_a_hidden_or_wholess_one()
    {
        var h = await WithRecordAsync(new() { ["title"] = "Q", ["status"] = "draft" });
        await h.PublishAsync(Lifecycle);
        var anonymous = new WorkflowActor("anonymous");
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ApplyTransitionAsync("application", "a1", "expire", null, anonymous))).Code);
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ApplyTransitionAsync("application", "a1", "expire", null, WorkflowActor.System))).Code);
        var rev = WorkflowActor.User("rev1", "Rana", ["reviewer"]);
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ApplyTransitionAsync("application", "a1", "expire", null, rev))).Code);
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, rev);
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ApplyTransitionAsync("application", "a1", "approve", null, rev))).Code);
        // App code moves records through the system API, which skips who (and allows hidden transitions).
        await h.Engine.ApplyTransitionAsSystemAsync("application", "a1", "approve");
        Assert.Equal("approved", h.Store.PeekRecord("application", "a1")!["status"]);
    }

    [Fact]
    public async Task Manual_starts_and_decisions_need_a_person()
    {
        var (h, task) = await OpenApprovalAsync();
        var anonymous = new WorkflowActor("anonymous");
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DecideAsync(task, anonymous, "approved", null, null))).Code);
        Assert.Equal("notAllowed", (await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.StartManualAsync("appr", "application", "a1", null, anonymous))).Code);
    }

    [Fact]
    public async Task Reading_a_record_creates_nothing()
    {
        var h = await WithRecordAsync(new() { ["title"] = "Q", ["status"] = "draft" });
        await h.PublishAsync(Lifecycle);
        var before = h.Store.All<AuditRecord>("audit").Count;
        h.Store.Tables.Remove("instances");
        var view = await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        Assert.Equal("draft", view.State);
        Assert.Contains(view.Transitions, t => t.Key == "submit");
        Assert.Empty(h.Store.All<InstanceRecord>("instances"));
        Assert.Equal(before, h.Store.All<AuditRecord>("audit").Count);
    }

    // ── Webhooks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_paths_encode_the_values_they_insert()
    {
        var h = await WithRecordAsync(new() { ["title"] = "a/../admin?x=1#", ["stage"] = "new" });
        await SettingsAsync(h, """{ "version": 1, "webhooks": { "allowedOrigins": ["https://api.example.com"] } }""");
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_w", "key": "hook", "kind": "automation", "title": "Hook", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "w", "type": "webhook", "url": "https://api.example.com", "method": "POST", "path": ["/items/", { "ref": "entity.title" }] } ] }
        """);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        Assert.Equal("https://api.example.com/items/a%2F..%2Fadmin%3Fx%3D1%23", Assert.Single(h.Webhooks.Calls).Url);
    }

    // ── Masking ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Form_answers_marked_sensitive_are_masked_in_step_outputs_and_views()
    {
        var h = await WithRecordAsync();
        await SettingsAsync(h, """{ "version": 1, "audit": { "maskedFields": ["input.ssn"] } }""");
        await h.PublishAsync(AllApproval);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        await h.SettleAsync();
        var task = h.Store.All<TaskRecord>("tasks").Single().Id;
        await h.Engine.DecideAsync(task, WorkflowActor.User("rev1", "Rana"), "approved", null, new() { ["ssn"] = "123-45-6789" });
        await h.Engine.DecideAsync(task, WorkflowActor.User("rev2", "Rafiq"), "approved", null, new() { ["ssn"] = "987-65-4321" });
        await h.SettleAsync();
        Assert.DoesNotContain(h.Store.All<AuditRecord>("audit"), a => (a.Data ?? "").Contains("6789") || (a.Data ?? "").Contains("4321"));
        var view = await h.Engine.GetInstanceAsync(Instance(h, "appr").Id);
        var json = Values.Serialize(view);
        Assert.DoesNotContain("4321", json);
        Assert.DoesNotContain("6789", json);
    }

    [Fact]
    public async Task Sensitive_values_copied_into_variables_and_triggers_are_masked()
    {
        var h = await WithRecordAsync(new() { ["title"] = "Q", ["stage"] = "new", ["applicantEmail"] = "ali@private.example", ["amount"] = 5000 });
        await SettingsAsync(h, """{ "version": 1, "audit": { "maskedFields": ["application.amount"] } }""");
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_v", "key": "copy", "kind": "automation", "title": "Copy", "entity": "application",
          "variables": [ { "id": "v", "key": "v", "type": "text" } ],
          "trigger": { "type": "fieldChanged", "field": "amount" },
          "steps": [ { "id": "s", "type": "setVariables", "values": [ { "variable": "v", "value": { "ref": "entity.applicantEmail" } } ] },
                     { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }
        """);
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["amount"] = 7654321 });
        await h.SettleAsync();
        var audit = string.Join("\n", h.Store.All<AuditRecord>("audit").Select(a => a.Data));
        Assert.DoesNotContain("ali@private.example", audit);
        Assert.DoesNotContain("7654321", audit);
        var view = Values.Serialize(await h.Engine.GetInstanceAsync(Instance(h, "copy").Id));
        Assert.DoesNotContain("ali@private.example", view);
        Assert.DoesNotContain("7654321", view);
    }

    [Fact]
    public async Task Test_runs_mask_sensitive_values_and_leave_out_unreadable_fields()
    {
        var h = await WithRecordAsync(new() { ["title"] = "Q", ["stage"] = "new", ["applicantEmail"] = "ali@private.example" });
        var def = """
        { "specVersion": 1, "id": "wf_s", "key": "sim", "kind": "automation", "title": "Sim", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "steps": [ { "id": "n", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "body": ["Mail ", { "ref": "entity.applicantEmail" }] } ] }
        """;
        var result = await h.Engine.SimulateAsync(def, new SimulationRequest { EntityId = "a1", Trigger = new() { ["from"] = "new", ["to"] = "checked" } }, Admin);
        Assert.Null(result.Error);
        var json = Values.Serialize(result);
        Assert.DoesNotContain("ali@private.example", json);
        Assert.Equal("***", result.Record!["applicantEmail"]);
    }

    // ── Limits ────────────────────────────────────────────────────────────

    [Fact]
    public void Durations_over_fifty_years_are_invalid()
    {
        Assert.True(IsoDuration.TryParse("P50Y", out _));
        Assert.False(IsoDuration.TryParse("P51Y", out _));
        Assert.False(IsoDuration.TryParse("PT99999999999999999999H", out _));
        Assert.Equal("badDuration", Assert.Throws<WorkflowException>(() => IsoDuration.Parse("P999999Y")).Code);
    }

    [Fact]
    public async Task Test_runs_replay_at_most_fifty_events()
    {
        var h = await WithRecordAsync();
        var req = new SimulationRequest { EntityId = "a1", Events = Enumerable.Range(0, 51).Select(_ => new SimulationEvent { Type = "advance", Duration = "PT1H" }).ToList() };
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.SimulateAsync(AllApproval, req, Admin));
        Assert.Equal("tooManyEvents", e.Code);
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purging_the_newest_entries_keeps_the_chain_going()
    {
        var h = await WithRecordAsync();
        await SettingsAsync(h, """{ "version": 1, "audit": { "retentionDays": { "settings": 1, "definition": 1 } } }""");
        h.Clock.Advance(TimeSpan.FromDays(3));
        Assert.True(await h.Engine.PurgeAuditAsync(Admin) > 0);
        await SettingsAsync(h, """{ "version": 1, "timeZone": "UTC" }""");
        var verification = await h.Engine.VerifyAuditAsync();
        Assert.True(verification.Valid, verification.Problem);
        var seqs = h.Store.All<AuditRecord>("audit").Select(a => a.Seq).OrderBy(s => s).ToList();
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);
    }

    [Fact]
    public async Task A_purge_does_not_excuse_deleting_other_entries_in_its_range()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_x", "key": "x", "kind": "automation", "title": "X", "entity": "application",
          "trigger": { "type": "created" }, "steps": [] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        await SettingsAsync(h, """{ "version": 1, "audit": { "retentionDays": { "definition": 1 } } }""");
        h.Clock.Advance(TimeSpan.FromDays(3));
        await h.Engine.PurgeAuditAsync(Admin);
        Assert.True((await h.Engine.VerifyAuditAsync()).Valid);
        // Tamper: remove an instance entry that sits between purged definition entries' sequence numbers.
        var victim = h.Store.All<AuditRecord>("audit").First(a => a.Category == "instance");
        var table = h.Store.Tables["audit"];
        table.Remove(table.Single(kv => ((AuditRecord)kv.Value).Seq == victim.Seq).Key);
        Assert.False((await h.Engine.VerifyAuditAsync()).Valid);
    }

    [Fact]
    public void The_verifier_rejects_repeated_sequence_numbers()
    {
        var a = new AuditRecord { Seq = 1, Id = "a", Category = "c", Action = "a", SubjectType = "s", SubjectId = "x", CorrelationId = "x", PrevHash = AuditChain.GenesisHash };
        a.Hash = AuditChain.ComputeHash(a);
        var b = new AuditRecord { Seq = 1, Id = "b", Category = "c", Action = "a", SubjectType = "s", SubjectId = "x", CorrelationId = "x", PrevHash = AuditChain.GenesisHash };
        b.Hash = AuditChain.ComputeHash(b);
        var r = AuditVerifier.Verify([a, b]);
        Assert.False(r.Valid);
        Assert.Equal(2, r.Checked);
    }

    // ── In-memory store ───────────────────────────────────────────────────

    [Fact]
    public async Task The_memory_store_refuses_a_write_to_an_instance_that_changed_since_it_was_read()
    {
        var store = new InMemoryWorkflowStore();
        await using (var s = await store.BeginAsync())
        {
            await s.AddInstanceAsync(new InstanceRecord { Id = "i1" });
            await s.CommitAsync();
        }
        await using var first = await store.BeginAsync();
        await using var second = await store.BeginAsync();
        var a = (await first.GetInstanceAsync("i1"))!;
        var b = (await second.GetInstanceAsync("i1"))!;
        a.Status = InstanceStatus.Completed;
        await first.UpdateInstanceAsync(a);
        await first.CommitAsync();
        b.Status = InstanceStatus.Cancelled;
        await second.UpdateInstanceAsync(b);
        await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => second.CommitAsync());
    }

    [Fact]
    public async Task The_memory_store_undoes_writes_after_a_savepoint()
    {
        var store = new InMemoryWorkflowStore();
        await using var s = await store.BeginAsync();
        await s.AddInstanceAsync(new InstanceRecord { Id = "kept" });
        Assert.True(await s.CreateSavepointAsync("sp"));
        await s.AddInstanceAsync(new InstanceRecord { Id = "undone" });
        await s.RollbackToSavepointAsync("sp");
        await s.CommitAsync();
        Assert.Equal(["kept"], store.All<InstanceRecord>("instances").Select(i => i.Id));
    }

    [Fact]
    public async Task Ids_are_read_page_by_page()
    {
        var h = await HostAsync();
        for (var i = 0; i < 7; i++) await h.Store.CreateRecordAsync("application", $"r{i}", new() { ["title"] = "x" });
        await using var s = await h.Store.BeginAsync();
        var first = await s.Entities.ListIdsAsync("application", null, 4);
        var rest = await s.Entities.ListIdsAsync("application", first[^1], 4);
        Assert.Equal(7, first.Concat(rest).Distinct().Count());
        Assert.Equal(3, rest.Count);
    }
}
