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
