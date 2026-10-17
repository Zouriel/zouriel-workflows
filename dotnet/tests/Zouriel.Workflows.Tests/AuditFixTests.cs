using System.Text.Json.Nodes;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;
using Xunit.Abstractions;

namespace Zouriel.Workflows.Tests;

/// <summary>Regressions from the final audit of the core engine: each test failed before its fix.</summary>
public sealed class AuditFixTests(ITestOutputHelper output)
{
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

    internal static readonly WorkflowActor Admin = WorkflowActor.User("admin", "Admin", ["admin"]);

    private void Dump(WorkflowTestHost h)
    {
        foreach (var i in h.Store.All<InstanceRecord>("instances").OrderBy(i => i.StartedAt))
            output.WriteLine($"{i.DefinitionKey} {i.Kind} {i.Reason} {i.Status} depth={i.Depth} {i.Error}");
    }

    // ── 1. The initiator in who is the record's, never the caller ───────────

    private const string InitiatorLifecycle = """
    { "specVersion": 1, "id": "lc_i", "key": "lci", "kind": "lifecycle", "title": "LC", "entity": "application",
      "lifecycle": { "field": "status", "mode": "enforce",
        "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
                    { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                    { "id": "w", "key": "withdrawn", "label": "Withdrawn", "category": "terminal" },
                    { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
        "transitions": [
          { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "field": "entity.applicant" } ] },
          { "id": "t2", "key": "withdraw", "label": "Withdraw", "from": ["s"], "to": "w", "who": [ { "initiator": true } ] },
          { "id": "t3", "key": "approve", "label": "Approve", "from": ["s"], "to": "o", "who": [ { "managerOf": "initiator" } ] } ] } }
    """;

    [Fact]
    public async Task Initiator_in_who_is_the_record_instances_initiator_not_the_caller()
    {
        var h = await HostAsync();
        await h.PublishAsync(InitiatorLifecycle);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "rev1" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, WorkflowActor.User("rev1", "Rana"));
        await h.SettleAsync();
        Assert.Equal("rev1", h.Store.All<InstanceRecord>("instances").Single(i => i.Kind == "record").InitiatorId);

        var stranger = WorkflowActor.User("fin1", "Farah");
        Assert.Empty((await h.Engine.GetRecordAsync("application", "a1", stranger)).Transitions);
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ApplyTransitionAsync("application", "a1", "withdraw", null, stranger));
        Assert.Equal("notAllowed", e.Code);
        await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.ApplyTransitionAsync("application", "a1", "approve", null, stranger));

        Assert.Equal(["approve"], (await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("head", "Hana"))).Transitions.Select(t => t.Key));
        Assert.Equal(["withdraw"], (await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("rev1", "Rana"))).Transitions.Select(t => t.Key));
        Assert.Equal("withdrawn", (await h.Engine.ApplyTransitionAsync("application", "a1", "withdraw", null, WorkflowActor.User("rev1", "Rana"))).State);
    }

    [Fact]
    public async Task A_record_created_by_a_person_has_them_as_initiator_and_manual_starts_use_it()
    {
        var h = await HostAsync();
        await h.PublishAsync(InitiatorLifecycle);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_m", "key": "chase", "kind": "automation", "title": "Chase", "entity": "application",
          "trigger": { "type": "manual", "label": "Chase", "who": [ { "initiator": true } ] },
          "steps": [ { "id": "v", "type": "setVariables", "values": [] } ] }
        """);
        using (WorkflowWriteScope.Begin(WorkflowActor.User("ali", "Ali"), 0))
            await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicant"] = "ali" });
        await h.SettleAsync();
        Assert.Equal("ali", h.Store.All<InstanceRecord>("instances").Single(i => i.Kind == "record").InitiatorId);
        Assert.Empty((await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("fin1", "Farah"))).Manual);
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.StartManualAsync("chase", "application", "a1", null, WorkflowActor.User("fin1", "Farah")));
        Assert.Equal("notAllowed", e.Code);
        Assert.Single((await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("ali", "Ali"))).Manual);
        await h.Engine.StartManualAsync("chase", "application", "a1", null, WorkflowActor.User("ali", "Ali"));
    }

    // ── 2. Child runs carry the depth of the move that started them ─────────

    [Fact]
    public async Task An_automation_and_a_lifecycle_feeding_each_other_are_stopped_by_loop_protection()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_x", "key": "lcx", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status", "mode": "enforce",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial",
                          "onEnter": [ { "id": "s1", "type": "setFields", "values": [ { "field": "stage", "value": { "const": "new" } } ] } ] },
                        { "id": "s", "key": "submitted", "label": "Sub", "category": "active",
                          "onEnter": [ { "id": "s2", "type": "setFields", "values": [ { "field": "stage", "value": { "const": "checked" } } ] } ] },
                        { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                             { "id": "t2", "key": "back", "label": "Back", "from": ["s"], "to": "d", "who": [ { "role": "reviewer" } ] },
                             { "id": "t3", "key": "approve", "label": "OK", "from": ["s"], "to": "o", "who": [ { "role": "reviewer" } ] } ] } }
        """);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_x", "key": "pingpong", "kind": "automation", "title": "PP", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" },
          "settings": { "concurrency": "parallel" },
          "steps": [ { "id": "b", "type": "branch",
             "paths": [ { "id": "p1", "label": "checked", "when": { "ref": "trigger.to", "op": "eq", "value": { "const": "checked" } },
                          "steps": [ { "id": "a1", "type": "applyTransition", "lifecycle": "lcx", "transition": "back", "ifNotAllowed": "skip" } ] } ],
             "otherwise": { "id": "p2", "label": "else", "steps": [ { "id": "a2", "type": "applyTransition", "lifecycle": "lcx", "transition": "submit", "ifNotAllowed": "skip" } ] } } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "done" });
        await h.SettleAsync();
        Dump(h);
        var runs = h.Store.All<InstanceRecord>("instances").Where(i => i.DefinitionKey == "pingpong").ToList();
        Assert.InRange(runs.Count, 1, 5);
        Assert.Contains(h.Store.All<AuditRecord>("audit"), a => a.Action == "suppressed" && a.Data!.Contains("maxChainDepth"));
        // Child runs started by a workflow's move are deeper than it, not 0.
        Assert.Contains(h.Store.All<InstanceRecord>("instances"), i => i.Kind == "run" && i.Depth > 0);
    }

    // ── 3. Sub-workflows starting each other stop at maxChainDepth ─────────

    [Fact]
    public async Task A_sub_workflow_chain_deeper_than_maxChainDepth_stops_in_an_incident()
    {
        var h = await HostAsync();
        await h.Engine.SaveSettingsAsync(new GlobalSettings { Defaults = new DefaultSettings { MaxChainDepth = 2 } }, 0, Admin);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_c", "key": "ccc", "kind": "automation", "title": "C", "trigger": { "type": "signal", "signal": "nightly" },
          "steps": [ { "id": "n", "type": "setVariables", "values": [] } ] }
        """);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_b", "key": "bbb", "kind": "automation", "title": "B", "trigger": { "type": "signal", "signal": "nightly" },
          "steps": [ { "id": "s", "type": "subWorkflow", "workflow": "ccc", "wait": false } ] }
        """);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_a", "key": "aaa", "kind": "automation", "title": "A", "trigger": { "type": "manual", "label": "Go" },
          "steps": [ { "id": "s", "type": "subWorkflow", "workflow": "bbb", "wait": false } ] }
        """);
        await h.Engine.StartManualAsync("aaa", null, null, null, WorkflowActor.User("ali", "Ali"));
        await h.SettleAsync();
        Dump(h);
        var b = h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == "bbb");
        Assert.Equal(InstanceStatus.Incident, b.Status);
        Assert.Contains("maxChainDepth", b.Error);
        Assert.DoesNotContain(h.Store.All<InstanceRecord>("instances"), i => i.DefinitionKey == "ccc");
    }

    // ── 4. Settings are validated on save and read leniently ──────────────

    public static TheoryData<string> BadSettings() =>
    [
        """{ "version": 1, "calendar": { "hours": ["09:00", "5pm"] } }""",
        """{ "version": 1, "calendar": { "hours": ["09:00"] } }""",
        """{ "version": 1, "calendar": { "holidays": ["2026-02-30"] } }""",
        """{ "version": 1, "calendar": { "days": ["monday"] } }""",
        """{ "version": 1, "calendar": { "days": ["mon", "mon"] } }""",
        """{ "version": 1, "defaults": { "maxChainDepth": 0 } }""",
        """{ "version": 1, "defaults": { "maxChainDepth": 50 } }""",
        """{ "version": 1, "defaults": { "forEachMax": 501 } }""",
        """{ "version": 1, "defaults": { "concurrency": "whenever" } }""",
        """{ "version": 1, "defaults": { "reminders": ["tomorrow"] } }""",
        """{ "version": 1, "defaults": { "retry": { "max": 11 } } }""",
        """{ "version": 1, "audit": { "retentionDays": { "step": 0 } } }""",
        """{ "version": 1, "audit": { "maskedFields": ["applicantEmail"] } }""",
        """{ "version": 1, "webhooks": { "allowedOrigins": ["https://api.example.com/path"] } }""",
        """{ "version": 1, "webhooks": { "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "https://crm.example.com", "auth": "secret" } ] } }""",
        """{ "version": 1, "webhooks": { "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "https://crm.example.com", "header": "X Bad" } ] } }""",
    ];

    [Theory]
    [MemberData(nameof(BadSettings))]
    public async Task Settings_with_a_bad_value_are_refused_on_save(string json)
    {
        var h = await HostAsync();
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.SaveSettingsAsync(GlobalSettings.Parse(json), 0, Admin));
        Assert.Equal("invalidSettings", e.Code);
        Assert.Equal(0, (await h.Engine.GetSettingsAsync()).Revision);
    }

    [Fact]
    public async Task Good_settings_still_save()
    {
        var h = await HostAsync();
        var json = """
        { "version": 1, "timeZone": "Europe/London", "calendar": { "days": ["mon", "tue"], "hours": ["08:30", "16:00"], "holidays": ["2026-12-25"] },
          "defaults": { "retry": { "max": 3, "backoff": "exponential", "initial": "PT1M" }, "reminders": ["P1D"], "concurrency": "parallel", "maxChainDepth": 7, "forEachMax": 100 },
          "webhooks": { "allowedOrigins": ["https://api.example.com", "http://localhost:8080"], "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "https://crm.example.com/v1", "auth": "secret", "secretName": "crm", "header": "X-Api-Key" } ] },
          "audit": { "retentionDays": { "step": 30 }, "maskedFields": ["application.notes", "input.reason"] }, "paused": [ { "entity": "document" } ] }
        """;
        Assert.Equal(1, await h.Engine.SaveSettingsAsync(GlobalSettings.Parse(json), 0, Admin));
    }

    [Fact]
    public async Task Bad_calendar_values_already_stored_never_stop_the_engine()
    {
        var logged = new List<string>();
        var h = await HostAsync(o => new WorkflowEngineOptions
        {
            Registry = o.Registry, Store = o.Store, Identity = o.Identity, Notifications = o.Notifications, Webhooks = o.Webhooks, Actions = o.Actions,
            Time = o.Time, RecordLinkTemplate = o.RecordLinkTemplate, Logger = new ListLogger(logged),
        });
        // Written straight to the store, as an older engine (without the checks) would have saved it.
        await using (var s = await h.Store.BeginAsync())
        {
            await s.SaveSettingsAsync(new SettingsRecord
            {
                Json = """{ "version": 1, "calendar": { "days": ["mon", "funday"], "hours": ["09:00", "5pm"], "holidays": ["2026-02-30", "2026-12-25"] } }""", Revision = 1,
            });
            await s.CommitAsync();
        }
        var (_, rev) = await h.Engine.GetSettingsAsync();
        Assert.Equal(1, rev);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_w", "key": "waiter", "kind": "automation", "title": "W", "entity": "application", "settings": { "calendar": "working" },
          "trigger": { "type": "created" }, "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT2H" } } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        Assert.Equal(InstanceStatus.Waiting, h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == "waiter").Status);
        Assert.Contains(logged, m => m.Contains("5pm"));
        Assert.Contains(logged, m => m.Contains("2026-02-30"));
        Assert.Contains(logged, m => m.Contains("funday"));
        // The settings can be fixed.
        Assert.Equal(2, await h.Engine.SaveSettingsAsync(new GlobalSettings(), rev, Admin));
    }
}

/// <summary>Regressions from the final audit, part 2.</summary>
public sealed class AuditFixTests2(ITestOutputHelper output)
{
    private static WorkflowActor Admin => AuditFixTests.Admin;
    private static Task<WorkflowTestHost> HostAsync() => AuditFixTests.HostAsync();

    private void Dump(WorkflowTestHost h)
    {
        foreach (var i in h.Store.All<InstanceRecord>("instances").OrderBy(i => i.StartedAt))
            output.WriteLine($"{i.DefinitionKey} {i.Kind} {i.Reason} {i.Status} v{i.Version} depth={i.Depth} {i.Error}");
    }

    // ── 5. A permissions change only moves records whose version has the same states ──

    [Fact]
    public async Task A_permissions_change_leaves_records_on_versions_with_other_states_alone()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_p", "key": "lcp", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status", "mode": "enforce",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
                        { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                        { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                             { "id": "t2", "key": "approve", "label": "Approve", "from": ["s"], "to": "o", "who": [ { "role": "reviewer" } ] } ] } }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_p", "key": "lcp", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status", "mode": "enforce",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
                        { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t2", "key": "approve", "label": "Approve", "from": ["d"], "to": "o", "who": [ { "role": "reviewer" } ] } ] } }
        """);
        await h.Store.CreateRecordAsync("application", "a2", new() { ["title"] = "New" });
        await h.SettleAsync();
        var r = await h.Engine.UpdatePermissionsAsync("lc_p", [("t2", new JsonArray(new JsonObject { ["role"] = "finance" }))], null, Admin);
        Dump(h);
        Assert.Equal(1, r.RecordsMoved);
        Assert.Equal(1, r.RecordsKept);
        var records = h.Store.All<InstanceRecord>("instances").Where(i => i.Kind == "record").ToDictionary(i => i.EntityId!);
        Assert.Equal(1, records["a1"].Version);
        Assert.Equal(3, records["a2"].Version);
        var view = await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        Assert.Equal("submitted", view.State);
        Assert.Contains(h.Store.All<AuditRecord>("audit"), a => a.Action == "permissionsChanged" && a.Data!.Contains("\"recordsKept\":1"));
    }

    // ── 6. Timers that keep failing back off and are parked; archiving stops a lifecycle's timers ──

    [Fact]
    public async Task Archiving_a_lifecycle_drops_its_record_timers_and_restoring_plans_them_again()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_a", "key": "lca", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status", "mode": "enforce",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial", "sla": { "within": "PT12H", "steps": [] } },
                        { "id": "x", "key": "rejected", "label": "Expired", "category": "terminal" } ],
            "transitions": [ { "id": "t1", "key": "expire", "label": "Expire", "from": ["d"], "to": "x", "auto": { "after": "P1D" } } ] } }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(13));
        Assert.Single(h.Store.All<AuditRecord>("audit"), a => a.Action == "slaBreached");
        await h.Engine.ArchiveAsync("lc_a", Admin);
        Assert.Empty(h.Store.All<BookmarkRecord>("bookmarks"));
        await h.AdvanceAsync(TimeSpan.FromDays(2));
        Assert.DoesNotContain(h.Store.All<AuditRecord>("audit"), a => a.Action == "timerFailed");
        Assert.Equal("draft", h.Store.PeekRecord("application", "a1")!["status"]);

        await h.Engine.RestoreAsync("lc_a", Admin);
        // Counted from when the record entered the state, so it is already due; the SLA that ran is not planned again.
        Assert.Equal(["auto:t1"], h.Store.All<BookmarkRecord>("bookmarks").Select(b => b.Purpose));
        await h.SettleAsync();
        Assert.Equal("rejected", h.Store.PeekRecord("application", "a1")!["status"]);
        Assert.Single(h.Store.All<AuditRecord>("audit"), a => a.Action == "slaBreached");
    }

    [Fact]
    public async Task A_timer_that_keeps_failing_backs_off_and_is_parked_with_an_incident()
    {
        var h = await HostAsync();
        h.Engine.MaxTimerAttempts = 3;
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_r", "key": "remind", "kind": "automation", "title": "Remind", "entity": "application",
          "trigger": { "type": "manual", "label": "Go" },
          "steps": [ { "id": "ap", "type": "approval", "assignees": [ { "user": "rev1" } ], "rule": { "type": "any" }, "due": "P5D", "reminders": ["P1D"],
                       "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
        """);
        var view = await h.Engine.StartManualAsync("remind", "application", "a1", null, WorkflowActor.User("ali", "Ali"));
        await h.SettleAsync();
        h.Identity.Broken["rev1"] = true;
        h.Clock.Advance(TimeSpan.FromDays(1));
        var lockedUntil = new List<DateTimeOffset?>();
        var t0 = h.Clock.GetUtcNow();
        for (var i = 0; i < 10; i++)
        {
            await h.Engine.ProcessTimersAsync();
            var b = h.Store.All<BookmarkRecord>("bookmarks").Single(x => x.Purpose == BookmarkPurposes.Reminder);
            lockedUntil.Add(b.LockedUntil);
            h.Clock.Advance(TimeSpan.FromMinutes(5));
        }
        // Backs off 1 minute after the first failure, 2 after the second (the 5-minute steps skip none of them).
        Assert.Equal(t0 + TimeSpan.FromMinutes(1), lockedUntil[0]);
        Assert.Equal(t0 + TimeSpan.FromMinutes(5) + TimeSpan.FromMinutes(2), lockedUntil[1]);
        var failed = h.Store.All<AuditRecord>("audit").Where(a => a.Action == "timerFailed").ToList();
        Assert.Equal(3, failed.Count);
        var reminder = h.Store.All<BookmarkRecord>("bookmarks").Single(x => x.Purpose == BookmarkPurposes.Reminder);
        Assert.Equal(3, reminder.Attempts);
        Assert.Equal(DateTimeOffset.MaxValue, reminder.LockedUntil);
        Assert.Contains("rev1", reminder.Error);
        h.Identity.Broken.TryRemove("rev1", out _);
        var inst = await h.Engine.GetInstanceAsync(view.Id);
        Assert.Contains("failed 3 times", inst.Error);
        Assert.Contains(h.Store.All<AuditRecord>("audit"), a => a.Action == "incident" && a.InstanceId == view.Id);
        // The task itself stays open.
        Assert.Equal(Storage.TaskStatus.Open, h.Store.All<TaskRecord>("tasks").Single().Status);
    }
}

/// <summary>Regressions from the final audit, part 3.</summary>
public sealed class AuditFixTests3(ITestOutputHelper output)
{
    private static Task<WorkflowTestHost> HostAsync(Func<WorkflowEngineOptions, WorkflowEngineOptions>? configure = null) => AuditFixTests.HostAsync(configure);

    private void Dump(WorkflowTestHost h)
    {
        foreach (var i in h.Store.All<InstanceRecord>("instances").OrderBy(i => i.StartedAt))
            output.WriteLine($"{i.DefinitionKey} {i.Kind} {i.Reason} {i.Status} v{i.Version} depth={i.Depth} {i.Error}");
    }

    private sealed class DeferringAction : IWorkflowAction, IWorkflowActionResolver
    {
        public IWorkflowAction? Resolve(ActionDescriptor action) => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
        {
            // Changes an entity on the app's data context and leaves saving it to the unit of work.
            PendingWritesStore.Current!.Defer(g => g.SetFieldsAsync("application", context.EntityId!, new Dictionary<string, object?> { ["priority"] = "high" }, ct));
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["ok"] = true });
        }
    }

    // ── 8. Record changes an action makes but does not save stay the workflow's ──

    [Fact]
    public async Task Changes_an_action_leaves_unsaved_are_saved_inside_its_write_scope()
    {
        var h = await HostAsync(o => new WorkflowEngineOptions
        {
            Registry = o.Registry, Store = new PendingWritesStore(o.Store), Identity = o.Identity, Notifications = o.Notifications, Webhooks = o.Webhooks,
            Actions = new DeferringAction(), Time = o.Time, RecordLinkTemplate = o.RecordLinkTemplate,
        });
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_p", "key": "prio", "kind": "automation", "title": "Prio", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "priority" },
          "steps": [ { "id": "a", "type": "action", "action": "crm.ping", "input": {} } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["priority"] = "low" });
        await h.SettleAsync();
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["priority"] = "normal" });
        await h.SettleAsync();
        Dump(h);
        Assert.Equal("high", h.Store.PeekRecord("application", "a1")!["priority"]);
        // The action's own write does not start the workflow again (it is the workflow's change, at depth 1).
        Assert.Single(h.Store.All<InstanceRecord>("instances"), i => i.DefinitionKey == "prio");
        var change = h.Store.All<OutboxRecord>("outbox").Where(o => o.Kind == OutboxKinds.Change).Select(o => EntityChange.FromJson(o.Payload))
            .Single(c => c.Fields.TryGetValue("priority", out var f) && Equals(f.To, "high"));
        Assert.Equal("wf_p", change.CausationDefinitionId);
        Assert.Equal(1, change.Depth);
        Assert.Equal("workflow", change.ActorType);
    }

    // ── 16. One record's change events are processed in order, also after a failure ──

    [Fact]
    public async Task A_newer_change_of_a_record_waits_while_an_older_one_is_failing()
    {
        var h = await HostAsync(o => new WorkflowEngineOptions
        {
            Registry = o.Registry, Store = new PendingWritesStore(o.Store), Identity = o.Identity, Notifications = o.Notifications, Webhooks = o.Webhooks,
            Actions = o.Actions, Time = o.Time, RecordLinkTemplate = o.RecordLinkTemplate,
        });
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_s", "key": "stages", "kind": "automation", "title": "Stages", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage" }, "settings": { "concurrency": "parallel" },
          "steps": [ { "id": "v", "type": "setVariables", "values": [] } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["stage"] = "new" });
        await h.SettleAsync();
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "checked" });
        h.Clock.Advance(TimeSpan.FromMilliseconds(5));
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["stage"] = "done" });
        PendingSession.FailTriggerListings = 1;
        await h.Engine.ProcessOutboxAsync();
        var rows = h.Store.All<OutboxRecord>("outbox").Where(o => o.ProcessedAt is null).OrderBy(o => o.CreatedAt).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].Attempts);
        Assert.NotNull(rows[0].Error);
        Assert.Equal(0, rows[1].Attempts);
        Assert.DoesNotContain(h.Store.All<InstanceRecord>("instances"), i => i.DefinitionKey == "stages");

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await h.SettleAsync();
        var order = h.Store.All<InstanceRecord>("instances").Where(i => i.DefinitionKey == "stages").OrderBy(i => i.StartedAt)
            .Select(i => (string?)Values.Text(TriggerInfo.FromJson(i.TriggerJson)!.To)).ToList();
        Assert.Equal(["checked", "done"], order);
    }

    // ── 17. A store refuses a second live record instance / running serial automation ──

    [Fact]
    public async Task The_store_refuses_two_units_storing_the_same_live_key()
    {
        var store = new InMemoryWorkflowStore();
        var key = InstanceRecord.RecordKey("lc", "application", "a1");
        await using var s1 = await store.BeginAsync();
        await using var s2 = await store.BeginAsync();
        await s1.AddInstanceAsync(new InstanceRecord { Id = "i1", Kind = InstanceKinds.Record, LiveKey = key });
        await s2.AddInstanceAsync(new InstanceRecord { Id = "i2", Kind = InstanceKinds.Record, LiveKey = key });
        await s1.CommitAsync();
        await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => s2.CommitAsync());
        // Once the first lets go of the key (cancelled), another may take it.
        await using var s3 = await store.BeginAsync();
        var i1 = (await s3.GetInstanceAsync("i1"))!;
        i1.LiveKey = null;
        await s3.UpdateInstanceAsync(i1);
        await s3.AddInstanceAsync(new InstanceRecord { Id = "i3", Kind = InstanceKinds.Record, LiveKey = key });
        await s3.CommitAsync();
    }

    [Fact]
    public async Task Live_keys_are_held_while_live_and_released_when_finished()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_k", "key": "lck", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status", "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t", "key": "approve", "label": "OK", "from": ["d"], "to": "o", "who": [ { "role": "reviewer" } ] } ] } }
        """);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_k", "key": "serialWait", "kind": "automation", "title": "S", "entity": "application", "trigger": { "type": "manual", "label": "Go" },
          "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        Assert.Equal(InstanceRecord.RecordKey("lc_k", "application", "a1"), h.Store.All<InstanceRecord>("instances").Single(i => i.Kind == "record").LiveKey);
        await h.Engine.StartManualAsync("serialWait", "application", "a1", null, WorkflowActor.User("ali", "Ali"));
        await h.Engine.StartManualAsync("serialWait", "application", "a1", null, WorkflowActor.User("ali", "Ali"));
        var runs = h.Store.All<InstanceRecord>("instances").Where(i => i.DefinitionKey == "serialWait").ToList();
        Assert.Single(runs, r => r.LiveKey == InstanceRecord.SerialKey("wf_k", "application", "a1"));
        Assert.Single(runs, r => r.Status == InstanceStatus.Queued && r.LiveKey is null);
        await h.AdvanceAsync(TimeSpan.FromHours(3));
        Assert.All(h.Store.All<InstanceRecord>("instances").Where(i => i.DefinitionKey == "serialWait"), r => Assert.Null(r.LiveKey));
        await h.Store.DeleteRecordAsync("application", "a1");
        await h.SettleAsync();
        Assert.Null(h.Store.All<InstanceRecord>("instances").Single(i => i.Kind == "record").LiveKey);
    }
}

/// <summary>Masking gaps from the final audit (spec §11).</summary>
public sealed class MaskingAuditTests
{
    private static Task<WorkflowTestHost> HostAsync() => AuditFixTests.HostAsync();

    [Fact]
    public async Task An_instance_error_quoting_a_sensitive_value_is_masked()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_e", "key": "ende", "kind": "automation", "title": "End", "entity": "application",
          "trigger": { "type": "created" },
          "steps": [ { "id": "e", "type": "end", "result": "failed", "reason": ["Bad email ", { "ref": "entity.applicantEmail" }] } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicantEmail"] = "secret@x.com" });
        await h.SettleAsync();
        var inst = h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == "ende");
        var view = await h.Engine.GetInstanceAsync(inst.Id);
        Assert.Equal("Bad email ***", view.Error);
        Assert.DoesNotContain(h.Store.All<AuditRecord>("audit"), a => (a.Data ?? "").Contains("secret@x.com"));
    }

    [Fact]
    public async Task Task_titles_and_instructions_quoting_sensitive_values_are_masked()
    {
        var h = await HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_t", "key": "ask", "kind": "automation", "title": "Ask", "entity": "application",
          "trigger": { "type": "created" },
          "steps": [ { "id": "t", "type": "approval", "title": "Check", "assignees": [ { "user": "rev1" } ], "rule": { "type": "any" }, "due": "P1D",
                       "instructions": ["Write to ", { "ref": "entity.applicantEmail" }], "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicantEmail"] = "secret@x.com" });
        await h.SettleAsync();
        var task = (await h.Engine.QueryTasksAsync(new TaskQuery(), null)).Items.Single();
        Assert.Equal("Write to ***", task.Instructions);
        var record = await h.Engine.GetRecordAsync("application", "a1", WorkflowActor.User("rev1", "Rana"));
        Assert.Equal("Write to ***", record.Tasks.Single().Instructions);
    }

    [Fact]
    public async Task A_sensitive_value_url_encoded_into_a_webhook_path_is_masked()
    {
        var h = await HostAsync();
        await h.Engine.SaveSettingsAsync(new GlobalSettings
        {
            Webhooks = new WebhookSettings { Connections = [new WebhookConnection { Key = "crm", Label = "CRM", BaseUrl = "https://crm.example.com" }] },
        }, 0, AuditFixTests.Admin);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_w", "key": "push", "kind": "automation", "title": "Push", "entity": "application",
          "trigger": { "type": "created" },
          "steps": [ { "id": "w", "type": "webhook", "connection": "crm", "method": "POST", "path": ["/people/", { "ref": "entity.applicantEmail" }] } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["applicantEmail"] = "secret@x.com" });
        await h.SettleAsync();
        Assert.Equal("https://crm.example.com/people/secret%40x.com", h.Webhooks.Calls.Single().Url);
        var inst = h.Store.All<InstanceRecord>("instances").Single(i => i.DefinitionKey == "push");
        var view = await h.Engine.GetInstanceAsync(inst.Id);
        var input = Values.Serialize(view.Steps!.Single(s => s.NodeId == "w").Input);
        Assert.DoesNotContain("secret", input);
        Assert.DoesNotContain(h.Store.All<AuditRecord>("audit"), a => (a.Data ?? "").Contains("secret"));
    }

    [Fact]
    public async Task The_reset_list_hides_values_of_fields_masked_in_settings()
    {
        var h = await HostAsync();
        await h.Engine.SaveSettingsAsync(new GlobalSettings { Audit = new AuditSettings { MaskedFields = ["application.notes"] } }, 0, AuditFixTests.Admin);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_m", "key": "lcm", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
                        { "id": "s", "key": "submitted", "label": "Sub", "category": "active",
                          "onEnter": [ { "id": "n", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "private remark" } } ] },
                                       { "id": "m", "type": "notify", "channel": "email", "to": [ { "user": "ali" } ], "subject": ["About ", { "ref": "entity.notes" }], "body": ["x"] } ] },
                        { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                             { "id": "t2", "key": "approve", "label": "OK", "from": ["s"], "to": "o", "who": [ { "role": "reviewer" } ] } ],
            "reset": { "who": [ { "role": "admin" } ] } } }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q", ["notes"] = "old remark" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        await h.SettleAsync();
        var preview = await h.Engine.GetResetPreviewAsync("application", "a1", AuditFixTests.Admin);
        var all = string.Join(" | ", preview.Items.Select(i => $"{i.Title} {i.Detail} {i.Warning}"));
        Assert.Contains("(hidden)", all);
        Assert.DoesNotContain("remark", all);
    }
}

/// <summary>Server-side form answers and runtime value checks from the final audit.</summary>
public sealed class FormAnswerAuditTests
{
    private const string Form = """
    { "schemaVersion": 1, "id": "f", "title": "F", "pages": [ { "id": "p", "elements": [
      { "kind": "field", "id": "kind", "key": "kind", "type": "select", "label": "Kind",
        "options": [ { "id": "o1", "value": "grant", "label": "Grant" }, { "id": "o2", "value": "loan", "label": "Loan" }, { "id": "o3", "value": "old", "label": "Old", "archived": true } ] },
      { "kind": "field", "id": "tags", "key": "tags", "type": "checkboxes", "label": "Tags",
        "options": [ { "id": "t1", "value": "a", "label": "A" }, { "id": "t2", "value": "b", "label": "B" } ] },
      { "kind": "field", "id": "rate", "key": "rate", "type": "number", "label": "Rate",
        "visibility": { "action": "show", "when": { "ref": "kind", "op": "eq", "value": { "const": "loan" } } } },
      { "kind": "field", "id": "x", "key": "x", "type": "number", "label": "X" },
      { "kind": "field", "id": "y", "key": "y", "type": "number", "label": "Y" },
      { "kind": "field", "id": "total", "key": "total", "type": "calculated", "label": "Total", "calc": { "op": "sum", "args": [ { "ref": "x" }, { "ref": "y" } ] } },
      { "kind": "field", "id": "src", "key": "source", "type": "hidden", "label": "Source", "defaultValue": "web" } ] } ] }
    """;

    private static JsonObject FormNode => JsonNode.Parse(Form)!.AsObject();

    [Fact]
    public void Answers_to_hidden_fields_unknown_keys_and_forged_calculations_are_not_kept()
    {
        var data = new Dictionary<string, object?>
        {
            ["kind"] = "grant", ["tags"] = new List<object?> { "a" }, ["rate"] = 99.0, ["x"] = 2.0, ["y"] = 3.0, ["total"] = 1000000.0,
            ["source"] = "forged", ["isAdmin"] = true,
        };
        var v = new Forms.FormValidator();
        Assert.Empty(v.Validate(FormNode, data, "2026-10-19"));
        var kept = ((Forms.IFormValidator)v).Clean(FormNode, data, "2026-10-19");
        Assert.Equal(["kind", "source", "tags", "total", "x", "y"], kept.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(5.0, kept["total"]);
        Assert.Equal("web", kept["source"]);
    }

    [Theory]
    [InlineData("""{ "kind": "gift" }""")]
    [InlineData("""{ "kind": "old" }""")]
    [InlineData("""{ "tags": ["a", "z"] }""")]
    public void Choices_outside_the_options_are_refused(string json)
    {
        var errors = new Forms.FormValidator().Validate(FormNode, (Dictionary<string, object?>)Values.FromJson(json)!, "2026-10-19");
        Assert.Contains(errors, e => e.Message.StartsWith("Choose one of the options", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manual_start_keeps_only_the_forms_answers_and_works_calculations_out_itself()
    {
        var h = await AuditFixTests.HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        var def = JsonNode.Parse("""
        { "specVersion": 1, "id": "wf_f", "key": "formed", "kind": "automation", "title": "Formed", "entity": "application",
          "trigger": { "type": "manual", "label": "Go" },
          "variables": [ { "id": "v1", "key": "total", "type": "number" }, { "id": "v2", "key": "admin", "type": "text" } ],
          "steps": [ { "id": "s", "type": "setVariables", "values": [ { "variable": "v1", "value": { "ref": "input.total" } }, { "variable": "v2", "value": { "ref": "input.isAdmin" } } ] },
                     { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } } ] }
        """)!.AsObject();
        def["trigger"]!["form"] = FormNode;
        await h.PublishAsync(def.ToJsonString());
        var view = await h.Engine.StartManualAsync("formed", "application", "a1",
            new() { ["kind"] = "grant", ["x"] = 2.0, ["y"] = 3.0, ["total"] = 1000000.0, ["isAdmin"] = "yes" }, WorkflowActor.User("ali", "Ali"));
        var vars = (Dictionary<string, object?>)Values.Normalize(view.Variables)!;
        Assert.Equal(5.0, vars["total"]);
        Assert.Null(vars["admin"]);
    }

    [Fact]
    public async Task A_value_read_into_an_enum_field_must_be_one_of_its_options()
    {
        var h = await AuditFixTests.HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_e", "key": "prio", "kind": "automation", "title": "Prio", "entity": "application",
          "trigger": { "type": "created" },
          "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "priority", "value": { "ref": "entity.title" } } ] } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "urgent!" });
        await h.Store.CreateRecordAsync("application", "a2", new() { ["title"] = "high" });
        await h.SettleAsync();
        var inst = h.Store.All<InstanceRecord>("instances").Where(i => i.DefinitionKey == "prio").ToDictionary(i => i.EntityId!);
        Assert.Equal(InstanceStatus.Incident, inst["a1"].Status);
        Assert.Contains("'urgent!' is not one of", inst["a1"].Error);
        Assert.Null(h.Store.PeekRecord("application", "a1")!["priority"]);
        Assert.Equal(InstanceStatus.Completed, inst["a2"].Status);
        Assert.Equal("high", h.Store.PeekRecord("application", "a2")!["priority"]);
    }

    [Fact]
    public async Task A_decision_comment_is_capped_like_remarks()
    {
        var h = await AuditFixTests.HostAsync();
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_a", "key": "ask", "kind": "automation", "title": "Ask", "entity": "application", "trigger": { "type": "manual", "label": "Go" },
          "steps": [ { "id": "a", "type": "approval", "assignees": [ { "user": "rev1" } ], "rule": { "type": "any" }, "due": "P1D", "outcomes": { "approved": [], "rejected": [], "timedOut": [] } } ] }
        """);
        await h.Engine.StartManualAsync("ask", "application", "a1", null, WorkflowActor.User("ali", "Ali"));
        var task = h.Store.All<TaskRecord>("tasks").Single();
        var e = await Assert.ThrowsAsync<WorkflowException>(() => h.Engine.DecideAsync(task.Id, WorkflowActor.User("rev1", "Rana"), "approved", new string('x', 2001), null));
        Assert.Equal("invalidInput", e.Code);
        await h.Engine.DecideAsync(task.Id, WorkflowActor.User("rev1", "Rana"), "approved", "  " + new string('x', 2000) + "  ", null);
    }
}

/// <summary>Business-logic gaps from the final audit.</summary>
public sealed class BusinessLogicAuditTests
{
    [Fact]
    public async Task Idempotency_keys_stay_the_same_across_retries_of_one_step()
    {
        var h = await AuditFixTests.HostAsync();
        await h.Engine.SaveSettingsAsync(new GlobalSettings { Webhooks = new WebhookSettings { AllowedOrigins = ["https://hooks.example.com"] } }, 0, AuditFixTests.Admin);
        h.Actions.Set("crm.ping", new Dictionary<string, object?> { ["ok"] = true }, failTimes: 1);
        h.Webhooks.Responses["https://hooks.example.com/"] = new Queue<WebhookResponse>([new WebhookResponse(500, null), new WebhookResponse(200, "{}")]);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_i", "key": "idem", "kind": "automation", "title": "Idem", "entity": "application", "trigger": { "type": "manual", "label": "Go" },
          "steps": [ { "id": "a", "type": "action", "action": "crm.ping", "input": {}, "retry": { "max": 2, "backoff": "fixed", "initial": "PT1M" } },
                     { "id": "w", "type": "webhook", "url": "https://hooks.example.com/x", "method": "POST", "retry": { "max": 2, "backoff": "fixed", "initial": "PT1M" } } ] }
        """);
        await h.Engine.StartManualAsync("idem", "application", "a1", null, WorkflowActor.User("ali", "Ali"));
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        var calls = h.Actions.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal([1, 2], calls.Select(c => c.Attempt));
        Assert.Equal(calls[0].IdempotencyKey, calls[1].IdempotencyKey);
        var hooks = h.Webhooks.Calls;
        Assert.Equal(2, hooks.Count);
        Assert.Equal(hooks[0].IdempotencyKey, hooks[1].IdempotencyKey);
        Assert.NotEqual(calls[0].IdempotencyKey, hooks[0].IdempotencyKey);
    }

    [Fact]
    public async Task Background_audits_of_refused_writes_do_not_pile_up()
    {
        var h = await AuditFixTests.HostAsync();
        await h.PublishAsync("""
        { "specVersion": 1, "id": "lc_b", "key": "lcb", "kind": "lifecycle", "title": "LC", "entity": "application",
          "lifecycle": { "field": "status", "mode": "enforce",
            "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" }, { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
            "transitions": [ { "id": "t", "key": "approve", "label": "OK", "from": ["d"], "to": "o", "who": [ { "role": "reviewer" } ] } ] } }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        for (var i = 0; i < 20; i++)
            await Assert.ThrowsAsync<WorkflowFieldOwnedException>(() => h.Store.ChangeRecordAsync("application", "a1", new() { ["status"] = "approved" }));
        for (var i = 0; i < 100 && h.Engine.PendingBackgroundWrites > 0; i++) await Task.Delay(20);
        Assert.Equal(0, h.Engine.PendingBackgroundWrites);
        Assert.Equal(20, h.Store.All<AuditRecord>("audit").Count(a => a.Action == "writeRefused"));
    }
}

/// <summary>Spec items the engine never produced, from the final audit.</summary>
public sealed class SpecGapAuditTests
{
    private const string Lc = """
    { "specVersion": 1, "id": "lc_r", "key": "lcr", "kind": "lifecycle", "title": "LC", "entity": "application",
      "lifecycle": { "field": "status",
        "states": [ { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
                    { "id": "s", "key": "submitted", "label": "Sub", "category": "active" },
                    { "id": "o", "key": "approved", "label": "OK", "category": "terminal" } ],
        "transitions": [ { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "role": "reviewer" } ] },
                         { "id": "t2", "key": "approve", "label": "OK", "from": ["s"], "to": "o", "who": [ { "role": "reviewer" } ] } ] } }
    """;

    [Fact]
    public async Task Renaming_a_state_or_transition_key_in_use_is_a_renamedInUse_warning()
    {
        var h = await AuditFixTests.HostAsync();
        await h.PublishAsync(Lc);
        await h.PublishAsync("""
        { "specVersion": 1, "id": "wf_m", "key": "mover", "kind": "automation", "title": "Mover", "entity": "application", "trigger": { "type": "manual", "label": "Go" },
          "steps": [ { "id": "a", "type": "applyTransition", "lifecycle": "lcr", "transition": "approve", "ifNotAllowed": "skip" } ] }
        """);
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "Q" });
        await h.SettleAsync();
        // Nothing is in "submitted" yet: renaming it is fine.
        var renamed = Lc.Replace("\"key\": \"submitted\"", "\"key\": \"underReview\"");
        Assert.DoesNotContain(await h.Engine.ValidateAsync(renamed), p => p.Code == "renamedInUse");
        await h.Engine.ApplyTransitionAsync("application", "a1", "submit", null, WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        await h.SettleAsync();
        var problems = await h.Engine.ValidateAsync(renamed.Replace("\"key\": \"approve\"", "\"key\": \"accept\""));
        Assert.Contains(problems, p => p is { Code: "renamedInUse", Severity: "warning", Node: "s" } && p.Message.Contains("1 record(s)"));
        Assert.Contains(problems, p => p is { Code: "renamedInUse", Severity: "warning", Node: "t2" });
        Assert.DoesNotContain(problems, p => p.Node == "t1");
    }
}

/// <summary>Webhook client hardening from the final audit.</summary>
public sealed class WebhookClientAuditTests
{
    [Theory]
    [InlineData("64:ff9b::7f00:1", true)]          // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a00:1", true)]           // NAT64 of 10.0.0.1
    [InlineData("64:ff9b:1::c0a8:101", true)]      // local-use NAT64 of 192.168.1.1
    [InlineData("2002:a9fe:a9fe::1", true)]        // 6to4 of 169.254.169.254
    [InlineData("2002:c0a8:101::1", true)]         // 6to4 of 192.168.1.1
    [InlineData("::7f00:1", true)]                 // IPv4-compatible 127.0.0.1
    [InlineData("::ffff:0:a00:1", true)]           // IPv4-translated 10.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", true)] // Teredo
    [InlineData("64:ff9b::808:808", false)]        // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1", false)]         // 6to4 of 8.8.8.8
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    public void Ipv6_forms_that_carry_an_ipv4_address_are_judged_by_it(string address, bool isPrivate) =>
        Assert.Equal(isPrivate, HttpWebhookClient.IsPrivate(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void The_guarded_handler_uses_no_proxy() => Assert.False(HttpWebhookClient.CreateHandler(new HttpWebhookClientOptions()).UseProxy);

    [Theory]
    [InlineData("X-Title", "a\r\nX-Admin: yes", false)]
    [InlineData("X-Title", "a\nb", false)]
    [InlineData("X Title", "a", false)]
    [InlineData("X-Title:", "a", false)]
    [InlineData("X-Title", "plain value\twith tab", true)]
    public void Header_names_and_values_are_checked(string name, string value, bool ok) => Assert.Equal(ok, HttpWebhookClient.IsValidHeader(name, value));

    [Fact]
    public async Task The_client_refuses_to_send_a_header_with_a_line_break()
    {
        using var client = new HttpWebhookClient(new HttpClient(new NeverHandler()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(new WebhookRequest("POST", "https://example.com/x",
            new Dictionary<string, string> { ["X-Title"] = "a\r\nX-Admin: yes" }, null, "k", null)));
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing may be sent.");
    }
}

/// <summary>Collects log messages for assertions.</summary>
internal sealed class ListLogger(List<string> into) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (into) into.Add(formatter(state, exception));
    }
}
