using System.Text.Json.Nodes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Testing;

/// <summary>A conformance fixture step did not produce what it expects; the message names the fixture, the step and the difference.</summary>
public sealed class ConformanceException(string message) : Exception(message);

/// <summary>
/// Runs a spec conformance fixture (<c>spec/fixtures/engine/*.json</c>). The same files run against
/// every engine implementation; see <c>spec/fixtures/README.md</c> for the format.
/// </summary>
public static class ConformanceRunner
{
    /// <summary>
    /// Runs one fixture file against a fresh <see cref="WorkflowTestHost"/>: loads its registry, users,
    /// settings, records and fake action/webhook behaviour, publishes its definitions, then plays each step
    /// and checks its expectations. Throws <see cref="ConformanceException"/> at the first mismatch.
    /// </summary>
    public static async Task RunAsync(string fixturePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(fixturePath)!;
        var fx = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath, ct))!.AsObject();
        var registryNode = fx["registry"] is JsonValue rv ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, rv.GetValue<string>()), ct)) : fx["registry"];
        var registry = WorkflowRegistry.FromJson(registryNode!.ToJsonString());
        var now = DateTimeOffset.Parse(fx["now"]?.GetValue<string>() ?? "2026-10-19T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var host = new WorkflowTestHost(registry, now);
        var usersNode = fx["users"] is JsonValue uv ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, uv.GetValue<string>()), ct)) : fx["users"];
        foreach (var u in usersNode as JsonArray ?? [])
        {
            host.Identity.Add(new WorkflowUser(u!["id"]!.GetValue<string>(), u["name"]?.GetValue<string>() ?? u["id"]!.GetValue<string>(), u["email"]?.GetValue<string>(),
                Strings(u["roles"]), Strings(u["groups"])), u["manager"]?.GetValue<string>());
        }
        if (fx["settings"] is JsonObject settings)
        {
            await host.Engine.SaveSettingsAsync(Settings.GlobalSettings.Parse(settings.ToJsonString()), 0, WorkflowActor.User("admin", "Admin"), ct);
        }
        foreach (var (entity, list) in fx["records"] as JsonObject ?? [])
        foreach (var r in list as JsonArray ?? [])
        {
            var values = (Dictionary<string, object?>)Values.FromJson(r)!;
            var id = Values.Text(values["id"]);
            values.Remove("id");
            await host.Store.CreateRecordAsync(entity, id, values, ct);
        }
        foreach (var (key, behaviour) in fx["actions"] as JsonObject ?? [])
        {
            host.Actions.Set(key, Values.FromJson(behaviour?["output"]) as Dictionary<string, object?>, behaviour?["failTimes"]?.GetValue<int>() ?? 0, behaviour?["failMessage"]?.GetValue<string>());
        }
        foreach (var (prefix, responses) in fx["webhooks"] as JsonObject ?? [])
        {
            host.Webhooks.Responses[prefix] = new Queue<WebhookResponse>((responses as JsonArray ?? []).Select(x => new WebhookResponse(x!["status"]!.GetValue<int>(), x["body"]?.ToJsonString())));
        }
        // Records exist (and their creation is processed) before the workflows are published.
        await host.SettleAsync(ct);
        foreach (var d in fx["definitions"] as JsonArray ?? []) await host.PublishAsync(d!.ToJsonString(), ct);
        await host.SettleAsync(ct);

        var i = 0;
        foreach (var step in fx["steps"] as JsonArray ?? [])
        {
            i++;
            var s = step!.AsObject();
            var label = $"step {i} ({string.Join(",", s.Select(kv => kv.Key))})";
            try
            {
                await RunStepAsync(host, s, ct);
            }
            catch (ConformanceException e)
            {
                throw new ConformanceException($"{Path.GetFileName(fixturePath)} › {label}: {e.Message}");
            }
        }
    }

    private static IReadOnlyList<string>? Strings(JsonNode? n) => (n as JsonArray)?.Select(x => x!.GetValue<string>()).ToList();

    private static WorkflowActor Actor(WorkflowTestHost host, JsonNode? n)
    {
        var id = n?.GetValue<string>();
        if (id is null) return WorkflowActor.System;
        var user = host.Identity.GetUserAsync(id).Result;
        return WorkflowActor.User(id, user?.Name ?? id, user?.Roles);
    }

    private static async Task RunStepAsync(WorkflowTestHost host, JsonObject s, CancellationToken ct)
    {
        var expectError = s["expectError"]?.GetValue<string>();
        try
        {
            await ActAsync(host, s, ct);
            if (expectError is not null) throw new ConformanceException($"expected error '{expectError}' but the step succeeded");
        }
        catch (WorkflowException e) when (expectError is not null)
        {
            if (e.Code != expectError) throw new ConformanceException($"expected error '{expectError}' but got '{e.Code}': {e.Message}");
            await host.SettleAsync(ct);
        }
        catch (WorkflowException e)
        {
            throw new ConformanceException($"unexpected error '{e.Code}': {e.Message}");
        }
        if (s["expect"] is JsonObject expect) await CheckAsync(host, expect, ct);
    }

    private static Dictionary<string, object?> Map(JsonNode? n) => Values.FromJson(n) as Dictionary<string, object?> ?? [];

    private static async Task ActAsync(WorkflowTestHost host, JsonObject s, CancellationToken ct)
    {
        var e = host.Engine;
        if (s["create"] is JsonObject c)
        {
            await host.Store.CreateRecordAsync(c["entity"]!.GetValue<string>(), c["id"]!.GetValue<string>(), Map(c["values"]), ct);
        }
        else if (s["change"] is JsonObject ch)
        {
            using var _ = ch["actor"] is null ? null : Changes.WorkflowWriteScope.Begin(Actor(host, ch["actor"]), 0);
            await host.Store.ChangeRecordAsync(ch["entity"]!.GetValue<string>(), ch["id"]!.GetValue<string>(), Map(ch["values"]), ct);
        }
        else if (s["delete"] is JsonObject del)
        {
            await host.Store.DeleteRecordAsync(del["entity"]!.GetValue<string>(), del["id"]!.GetValue<string>(), ct);
        }
        else if (s["transition"] is JsonObject tr)
        {
            await e.ApplyTransitionAsync(tr["entity"]!.GetValue<string>(), tr["id"]!.GetValue<string>(), tr["transition"]!.GetValue<string>(), Map(tr["input"]),
                Actor(host, tr["actor"]), tr["lifecycle"]?.GetValue<string>(), ct, tr["comment"]?.GetValue<string>());
        }
        else if (s["reset"] is JsonObject rs)
        {
            var (re, rid, actor) = (rs["entity"]!.GetValue<string>(), rs["id"]!.GetValue<string>(), Actor(host, rs["actor"]));
            // "undo": explicit item ids, or "undoSelected": true for whatever the reset list ticks by default.
            var undo = (rs["undo"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList();
            if (rs["undoSelected"]?.GetValue<bool>() == true)
                undo = (await e.GetResetPreviewAsync(re, rid, actor, rs["lifecycle"]?.GetValue<string>(), ct)).Items.Where(i => i.Selected).Select(i => i.Id).ToList();
            await e.ResetAsync(re, rid, Map(rs["input"]), actor, rs["lifecycle"]?.GetValue<string>(), ct, undo, comment: rs["comment"]?.GetValue<string>());
        }
        else if (s["forceState"] is JsonObject fs)
        {
            await e.ForceStateAsync(fs["entity"]!.GetValue<string>(), fs["id"]!.GetValue<string>(), fs["state"]!.GetValue<string>(), fs["reason"]?.GetValue<string>() ?? "",
                fs["skipSteps"]?.GetValue<bool>() ?? false, Actor(host, fs["actor"]), null, ct);
        }
        else if (s["decide"] is JsonObject d)
        {
            var task = await FindTaskAsync(host, d["step"]?.GetValue<string>(), d["task"]?.GetValue<int>(), ct);
            await e.DecideAsync(task.Id, Actor(host, d["user"]), d["outcome"]!.GetValue<string>(), d["comment"]?.GetValue<string>(), Map(d["data"]), ct);
        }
        else if (s["delegate"] is JsonObject dg)
        {
            var task = await FindTaskAsync(host, dg["step"]?.GetValue<string>(), null, ct);
            await e.DelegateAsync(task.Id, Actor(host, dg["user"]), dg["to"]!.GetValue<string>(), null, ct);
        }
        else if (s["signal"] is JsonObject sig)
        {
            await e.SignalAsync(sig["signal"]!.GetValue<string>(), sig["entity"]?.GetValue<string>(), sig["id"]?.GetValue<string>(), Map(sig["data"]), null, ct);
        }
        else if (s["manual"] is JsonObject m)
        {
            await e.StartManualAsync(m["workflow"]!.GetValue<string>(), m["entity"]?.GetValue<string>(), m["id"]?.GetValue<string>(), Map(m["input"]), Actor(host, m["actor"]), ct);
        }
        else if (s["advance"] is JsonValue adv)
        {
            await host.AdvanceAsync(IsoDuration.Parse(adv.GetValue<string>()).Approximate, ct);
        }
        else if (s["publish"] is JsonObject pub)
        {
            await host.PublishAsync(pub.ToJsonString(), ct);
        }
        else if (s["retry"] is JsonObject rt)
        {
            await e.RetryAsync((await FindInstanceAsync(host, rt["definition"]!.GetValue<string>(), ct)).Id, Actor(host, JsonValue.Create("admin")), rt["step"]?.GetValue<string>(), ct);
        }
        else if (s["skip"] is JsonObject sk)
        {
            await e.SkipAsync((await FindInstanceAsync(host, sk["definition"]!.GetValue<string>(), ct)).Id, Map(sk["outputs"]), Actor(host, JsonValue.Create("admin")), sk["step"]?.GetValue<string>(), ct);
        }
        else if (s["cancel"] is JsonObject cn)
        {
            await e.CancelAsync((await FindInstanceAsync(host, cn["definition"]!.GetValue<string>(), ct)).Id, "test", Actor(host, JsonValue.Create("admin")), ct);
        }
        else if (s["migrate"] is JsonObject mg)
        {
            var inst = (await e.QueryInstancesAsync(new InstanceQuery { DefinitionKey = mg["definition"]!.GetValue<string>(), EntityId = mg["id"]?.GetValue<string>(), Kinds = [InstanceKinds.Record, InstanceKinds.Automation] }, ct)).Items[0];
            var map = (mg["stateMap"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
            await e.MigrateAsync(inst.Id, mg["version"]!.GetValue<int>(), map, Actor(host, JsonValue.Create("admin")), ct);
        }
        else if (s["settings"] is JsonObject st)
        {
            var (_, rev) = await e.GetSettingsAsync(ct);
            await e.SaveSettingsAsync(Settings.GlobalSettings.Parse(st.ToJsonString()), rev, Actor(host, JsonValue.Create("admin")), ct);
        }
        else if (s["archive"] is JsonValue ar)
        {
            var def = (await e.ListDefinitionsAsync(ct)).First(d => d.Key == ar.GetValue<string>());
            await e.ArchiveAsync(def.Id, Actor(host, JsonValue.Create("admin")), ct);
        }
        else if (s["expect"] is null)
        {
            throw new ConformanceException($"unknown step: {s.ToJsonString()}");
        }
        await host.SettleAsync(ct);
    }

    private static async Task<TaskView> FindTaskAsync(WorkflowTestHost host, string? step, int? index, CancellationToken ct)
    {
        var all = (await host.Engine.QueryTasksAsync(new TaskQuery { Take = 1000 }, null, ct)).Items.OrderBy(t => t.CreatedAt).ToList();
        var tasks = all.Where(t => step is null || TaskStep(host, t) == step).ToList();
        if (index is not null) return tasks.ElementAtOrDefault(index.Value) ?? throw new ConformanceException($"no task #{index} for step '{step}'");
        // The open task, else the latest one (so deciding a closed task can be tested).
        return tasks.FirstOrDefault(t => t.Status == Storage.TaskStatus.Open) ?? tasks.LastOrDefault() ?? throw new ConformanceException($"no task for step '{step}'");
    }

    private static string TaskStep(WorkflowTestHost host, TaskView t) =>
        host.Store.All<TaskRecord>("tasks").First(r => r.Id == t.Id).NodeId;

    private static async Task<InstanceView> FindInstanceAsync(WorkflowTestHost host, string key, CancellationToken ct) =>
        InCreationOrder(host, (await host.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = key, Take = 1000 }, ct)).Items).LastOrDefault()
        ?? throw new ConformanceException($"no instance of '{key}'");

    /// <summary>Instances in the order they were created (the audit sequence breaks ties between equal clock times).</summary>
    private static List<InstanceView> InCreationOrder(WorkflowTestHost host, IEnumerable<InstanceView> items)
    {
        var first = new Dictionary<string, long>();
        foreach (var a in host.Store.All<AuditRecord>("audit").OrderBy(a => a.Seq))
        {
            var key = a.InstanceId ?? (a.SubjectType == "record" ? null : a.SubjectId);
            if (key is not null && !first.ContainsKey(key)) first[key] = a.Seq;
        }
        return items.OrderBy(i => i.StartedAt).ThenBy(i => first.GetValueOrDefault(i.Id, long.MaxValue)).ToList();
    }

    // ── Expectations ──────────────────────────────────────────────────────

    private static async Task CheckAsync(WorkflowTestHost host, JsonObject expect, CancellationToken ct)
    {
        var e = host.Engine;
        if (expect["instances"] is JsonArray want)
        {
            var kinds = expect["instanceKinds"] is JsonArray ks ? ks.Select(k => k!.GetValue<string>()).ToList() : [InstanceKinds.Automation, InstanceKinds.Record];
            var got = InCreationOrder(host, (await e.QueryInstancesAsync(new InstanceQuery { Take = 10000, Kinds = kinds }, ct)).Items);
            var actual = got.Select(i => new Dictionary<string, object?>
            {
                ["definition"] = i.DefinitionKey, ["kind"] = i.Kind, ["status"] = i.Status, ["state"] = i.State, ["record"] = i.EntityId, ["version"] = (double)i.Version,
                ["error"] = i.Error, ["reason"] = i.Reason,
            }).ToList();
            CompareList("instances", want, actual);
        }
        if (expect["record"] is JsonObject rec)
        {
            var snap = host.Store.PeekRecord(rec["entity"]!.GetValue<string>(), rec["id"]!.GetValue<string>());
            if (rec["deleted"]?.GetValue<bool>() == true)
            {
                if (snap is not null) throw new ConformanceException("record: expected it to be deleted");
            }
            else
            {
                if (snap is null) throw new ConformanceException("record: does not exist");
                CompareSubset("record", rec["values"], snap.Values);
            }
        }
        if (expect["tasks"] is JsonArray wantTasks)
        {
            var tasks = (await e.QueryTasksAsync(new TaskQuery { Take = 1000 }, null, ct)).Items.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
            var records = host.Store.All<TaskRecord>("tasks").ToDictionary(r => r.Id);
            tasks = tasks.OrderBy(t => records[t.Id].CreatedAt).ThenBy(t => host.Store.All<AuditRecord>("audit").FirstOrDefault(a => a.SubjectId == t.Id && a.Action == "created")?.Seq ?? 0).ToList();
            var actual = tasks.Select(t => new Dictionary<string, object?>
            {
                ["step"] = records[t.Id].NodeId, ["status"] = t.Status, ["assignees"] = t.Assignees.Select(a => (object?)a).ToList(), ["outcome"] = t.Outcome,
                ["title"] = t.Title, ["due"] = t.DueAt, ["instructions"] = t.Instructions, ["escalationLevel"] = (double)t.EscalationLevel,
            }).ToList();
            CompareList("tasks", wantTasks, actual);
        }
        if (expect["notifications"] is JsonArray wantN)
        {
            var actual = host.Notifications.Sent.Select(n => new Dictionary<string, object?>
            {
                ["channel"] = n.Channel, ["to"] = n.To.Select(r => (object?)(r.UserId ?? r.Email)).ToList(), ["subject"] = n.Subject, ["body"] = n.Body, ["link"] = n.Link,
            }).ToList();
            CompareList("notifications", wantN, actual);
        }
        if (expect["notificationCount"] is JsonValue nc && host.Notifications.Sent.Count != nc.GetValue<int>())
            throw new ConformanceException($"notificationCount: expected {nc.GetValue<int>()}, got {host.Notifications.Sent.Count}: {string.Join(" | ", host.Notifications.Sent.Select(n => n.Subject))}");
        if (expect["actionsCalled"] is JsonArray wantA)
        {
            var actual = host.Actions.Calls.Select(c => new Dictionary<string, object?> { ["key"] = c.ActionKey, ["input"] = c.Input.ToDictionary(kv => kv.Key, kv => kv.Value), ["attempt"] = (double)c.Attempt }).ToList();
            CompareList("actionsCalled", wantA, actual);
        }
        if (expect["webhooks"] is JsonArray wantW)
        {
            var actual = host.Webhooks.Calls.Select(c => new Dictionary<string, object?>
            {
                ["method"] = c.Method, ["url"] = c.Url, ["body"] = c.Body is null ? null : Values.FromJson(c.Body), ["headers"] = c.Headers.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            }).ToList();
            CompareList("webhooks", wantW, actual);
        }
        if (expect["outputs"] is JsonObject wantO)
        {
            foreach (var (key, steps) in wantO)
            {
                var inst = await FindInstanceAsync(host, key, ct);
                var view = await e.GetInstanceAsync(inst.Id, ct);
                CompareSubset($"outputs.{key}", steps, Values.Normalize(view.Outputs));
            }
        }
        if (expect["vars"] is JsonObject wantV)
        {
            foreach (var (key, vars) in wantV)
            {
                var view = await e.GetInstanceAsync((await FindInstanceAsync(host, key, ct)).Id, ct);
                CompareSubset($"vars.{key}", vars, Values.Normalize(view.Variables));
            }
        }
        if (expect["available"] is JsonObject av)
        {
            var view = await e.GetRecordAsync(av["entity"]!.GetValue<string>(), av["id"]!.GetValue<string>(), Actor(host, av["user"]), ct);
            var keys = view.Transitions.Select(t => (object?)t.Key).ToList();
            CompareValue("available.transitions", Values.FromJson(av["transitions"]), keys);
            if (av["state"] is JsonValue st && view.State != st.GetValue<string>()) throw new ConformanceException($"available.state: expected {st}, got {view.State}");
            if (av["reset"] is JsonValue rv && (view.Reset is not null) != rv.GetValue<bool>())
                throw new ConformanceException($"available.reset: expected {rv}, got {view.Reset is not null}");
            // "comments": { transitionKey: "none"|"optional"|"required" } for the offered transitions; "resetComment" for the reset option.
            if (av["comments"] is JsonObject wantC)
                CompareSubset("available.comments", wantC, view.Transitions.ToDictionary(t => t.Key, t => (object?)t.Comment));
            if (av["resetComment"] is JsonValue rc && view.Reset?.Comment != rc.GetValue<string>())
                throw new ConformanceException($"available.resetComment: expected {rc}, got {view.Reset?.Comment ?? "no reset"}");
        }
        if (expect["resetPreview"] is JsonObject rp)
        {
            // The reset list, item by item: { id, kind?, selected?, undoable? } in order; or { error } when it is refused.
            if (rp["error"] is JsonValue wantError)
            {
                try
                {
                    await e.GetResetPreviewAsync(rp["entity"]!.GetValue<string>(), rp["id"]!.GetValue<string>(), Actor(host, rp["user"]), null, ct);
                    throw new ConformanceException($"resetPreview: expected error '{wantError}' but it was listed");
                }
                catch (WorkflowException ex) when (ex.Code == wantError.GetValue<string>()) { }
                catch (WorkflowException ex) { throw new ConformanceException($"resetPreview: expected error '{wantError}' but got '{ex.Code}'"); }
            }
            else
            {
            var preview = await e.GetResetPreviewAsync(rp["entity"]!.GetValue<string>(), rp["id"]!.GetValue<string>(), Actor(host, rp["user"]), null, ct);
            var actual = preview.Items.Select(i => (object?)new Dictionary<string, object?>
            {
                ["id"] = i.Id, ["kind"] = i.Kind, ["selected"] = i.Selected, ["undoable"] = i.Undoable, ["title"] = i.Title, ["detail"] = i.Detail,
            }).ToList();
            var wantItems = rp["items"]!.AsArray();
            if (wantItems.Count != actual.Count)
                throw new ConformanceException($"resetPreview.items: expected {wantItems.Count}, got {actual.Count}: {string.Join(", ", preview.Items.Select(i => i.Id))}");
            for (var k = 0; k < wantItems.Count; k++) CompareSubset($"resetPreview.items[{k}]", wantItems[k], actual[k]);
            }
        }
        if (expect["audit"] is JsonArray wantAudit)
        {
            // A subsequence check: these entries appear in this order (others may be in between).
            var entries = host.Store.All<AuditRecord>("audit").OrderBy(a => a.Seq).ToList();
            var pos = 0;
            foreach (var w in wantAudit)
            {
                var o = w is JsonValue wv ? new JsonObject { ["is"] = wv.GetValue<string>() } : w!.AsObject();
                var target = o["is"]!.GetValue<string>();
                var found = false;
                for (; pos < entries.Count; pos++)
                {
                    var a = entries[pos];
                    if ($"{a.Category}.{a.Action}" != target) continue;
                    if (o["data"] is JsonObject wd && !SubsetMatches(wd, a.Data is null ? null : Values.FromJson(a.Data))) continue;
                    if (o["actor"] is JsonValue wa && a.ActorId != wa.GetValue<string>() && a.ActorType != wa.GetValue<string>()) continue;
                    found = true;
                    pos++;
                    break;
                }
                if (!found) throw new ConformanceException($"audit: missing '{o.ToJsonString()}' (in order). Have: {string.Join(", ", entries.Select(a => $"{a.Category}.{a.Action}"))}");
            }
        }
        if (expect["auditCount"] is JsonObject counts)
        {
            var entries = host.Store.All<AuditRecord>("audit");
            foreach (var (k, v) in counts)
            {
                var n = entries.Count(a => $"{a.Category}.{a.Action}" == k);
                if (n != v!.GetValue<int>()) throw new ConformanceException($"auditCount.{k}: expected {v}, got {n}");
            }
        }
        if (expect["auditChainValid"]?.GetValue<bool>() == true)
        {
            var result = await e.VerifyAuditAsync(1, ct);
            if (!result.Valid) throw new ConformanceException($"audit chain broken at {result.BrokenAtSeq}: {result.Problem}");
        }
        if (expect["bookmarks"] is JsonValue bc)
        {
            var n = host.Store.All<BookmarkRecord>("bookmarks").Count;
            if (n != bc.GetValue<int>()) throw new ConformanceException($"bookmarks: expected {bc}, got {n}: {string.Join(", ", host.Store.All<BookmarkRecord>("bookmarks").Select(b => b.Purpose))}");
        }
    }

    private static bool SubsetMatches(JsonNode? want, object? actual)
    {
        try
        {
            CompareValue("", Values.FromJson(want), actual);
            return true;
        }
        catch (ConformanceException)
        {
            return false;
        }
    }

    private static void CompareList(string path, JsonArray want, List<Dictionary<string, object?>> actual)
    {
        if (want.Count != actual.Count)
            throw new ConformanceException($"{path}: expected {want.Count} item(s), got {actual.Count}: {Values.Serialize(actual.Select(Compact).ToList())}");
        for (var i = 0; i < want.Count; i++) CompareSubset($"{path}[{i}]", want[i], actual[i]);
    }

    private static Dictionary<string, object?> Compact(Dictionary<string, object?> d) => d.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static void CompareSubset(string path, JsonNode? want, object? actual) => CompareValue(path, Values.FromJson(want), actual);

    /// <summary>Objects in the expectation are subsets (extra actual keys are fine); lists and scalars must match exactly.</summary>
    private static void CompareValue(string path, object? want, object? actual)
    {
        actual = Values.Normalize(actual);
        switch (want)
        {
            case Dictionary<string, object?> w:
                if (actual is not Dictionary<string, object?> a) throw new ConformanceException($"{path}: expected an object, got {Values.Serialize(actual)}");
                foreach (var (k, v) in w) CompareValue($"{path}.{k}", v, a.GetValueOrDefault(k));
                return;
            case List<object?> wl:
                if (actual is not List<object?> al || al.Count != wl.Count) throw new ConformanceException($"{path}: expected {Values.Serialize(wl)}, got {Values.Serialize(actual)}");
                for (var i = 0; i < wl.Count; i++) CompareValue($"{path}[{i}]", wl[i], al[i]);
                return;
            default:
                if (!Values.DeepEquals(want, actual)) throw new ConformanceException($"{path}: expected {Values.Serialize(want)}, got {Values.Serialize(actual)}");
                return;
        }
    }
}
