using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>
/// Stuck instances through the REST API (spec §10): a timer that keeps failing is parked and its record instance goes to
/// <c>incident</c> (the record view shows the status and error, and still offers moves); a lifecycle child run whose step
/// failed is stuck too; <c>GET /instances?status=incident</c> lists both for people who may intervene, and retry
/// re-arms the timer. Uses its own database because it changes the lifecycle and makes the database refuse writes.
/// </summary>
public sealed class IncidentApiTests : IClassFixture<ApiTests.App>
{
    private readonly ApiTests.App _app;

    public IncidentApiTests(ApiTests.App app) => _app = app;

    private HttpClient As(string user)
    {
        var c = _app.CreateClient();
        c.DefaultRequestHeaders.Add("X-Demo-User", user);
        return c;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task SqlAsync(string sql)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SampleDb>().Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>Polls until <paramref name="done"/> holds (the background worker runs every 500 ms).</summary>
    private static async Task<JsonNode> UntilAsync(Func<Task<JsonNode>> read, Func<JsonNode, bool> done)
    {
        JsonNode last = null!;
        for (var i = 0; i < 40; i++)
        {
            last = await read();
            if (done(last)) return last;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out; last: {last.ToJsonString()}");
        return last;
    }

    private async Task<JsonNode> StuckAsync() => await Json(await As("hana").GetAsync("/api/workflows/instances?status=incident"));

    [Fact]
    public async Task Stuck_instances_are_listed_their_records_still_move_and_retry_re_arms_a_parked_timer()
    {
        // The lifecycle gets an automatic move after a second (only for the "Stuck grant") and a Submitted step the database refuses.
        var draft = (await Json(await As("hana").GetAsync("/api/workflows/definitions/wf_scholarship")))["draft"]!.AsObject();
        draft["lifecycle"]!["transitions"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "tr_auto", "key": "autoWithdraw", "label": "Withdraw stuck drafts", "from": ["st_draft"], "to": "st_withdrawn", "hidden": true,
              "auto": { "after": "PT1S" }, "when": { "ref": "entity.title", "op": "eq", "value": { "const": "Stuck grant" } } }
            """));
        var submitted = draft["lifecycle"]!["states"]!.AsArray().First(s => s!["id"]!.GetValue<string>() == "st_submitted")!;
        submitted["onEnter"]!.AsArray().Add(JsonNode.Parse("""{ "id": "s_boom", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "boom" } } ] }"""));
        await Json(await As("hana").PutAsync("/api/workflows/definitions/wf_scholarship", JsonContent.Create(draft)));
        await Json(await As("hana").PostAsJsonAsync("/api/workflows/definitions/wf_scholarship/publish", new { note = "incident test" }));
        await SqlAsync("CREATE TRIGGER no_withdraw BEFORE UPDATE ON Applications WHEN NEW.Status = 'Withdrawn' AND NEW.Title = 'Stuck grant' BEGIN SELECT RAISE(ABORT, 'the registry refuses'); END;");
        await SqlAsync("CREATE TRIGGER no_boom BEFORE UPDATE ON Applications WHEN NEW.Notes = 'boom' BEGIN SELECT RAISE(ABORT, 'notes may not be boom'); END;");
        _app.Services.GetRequiredService<WorkflowEngine>().MaxTimerAttempts = 1;

        // 1. A timer that fails is parked: its record instance is stuck, and the record view says so.
        var stuck = (await Json(await As("sara").PostAsJsonAsync("/api/demo/applications", new { title = "Stuck grant", amount = 100, programmeId = 1 })))["id"]!.GetValue<int>();
        var list = await UntilAsync(StuckAsync, l => l["items"]!.AsArray().Any(i => i!["entityId"]!.GetValue<string>() == stuck.ToString() && i["kind"]!.GetValue<string>() == "record"));
        var record = list["items"]!.AsArray().First(i => i!["entityId"]!.GetValue<string>() == stuck.ToString())!;
        Assert.Contains("auto:tr_auto", record["error"]!.GetValue<string>());
        var view = await Json(await As("sara").GetAsync($"/api/workflows/records/application/{stuck}"));
        Assert.Equal("incident", view["status"]!.GetValue<string>());
        Assert.Contains("failed 1 times", view["error"]!.GetValue<string>());
        Assert.Contains(view["transitions"]!.AsArray(), t => t!["key"]!.GetValue<string>() == "submit"); // not frozen

        // 2. A lifecycle child run whose step failed is listed too (kind run), without asking for runs.
        var boom = (await Json(await As("sara").PostAsJsonAsync("/api/demo/applications", new { title = "Boom grant", amount = 100, programmeId = 1 })))["id"]!.GetValue<int>();
        await Task.Delay(1000);
        await Json(await As("sara").PostAsJsonAsync($"/api/workflows/records/application/{boom}/transitions/submit", new { }));
        list = await UntilAsync(StuckAsync, l => l["items"]!.AsArray().Any(i => i!["entityId"]!.GetValue<string>() == boom.ToString()));
        Assert.Equal("run", list["items"]!.AsArray().First(i => i!["entityId"]!.GetValue<string>() == boom.ToString())!["kind"]!.GetValue<string>());

        // 3. Retry re-arms the parked timer once the cause is fixed: the instance is active again and the move happens.
        await SqlAsync("DROP TRIGGER no_withdraw;");
        var retried = await Json(await As("hana").PostAsJsonAsync($"/api/workflows/instances/{record["id"]}/retry", new { }));
        Assert.Equal("active", retried["status"]!.GetValue<string>());
        Assert.Null(retried["error"]);
        var moved = await UntilAsync(async () => await Json(await As("sara").GetAsync($"/api/workflows/records/application/{stuck}")), v => v["state"]!.GetValue<string>() == "Withdrawn");
        Assert.Null(moved["error"]);
        Assert.DoesNotContain((await StuckAsync())["items"]!.AsArray(), i => i!["entityId"]!.GetValue<string>() == stuck.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await As("sara").PostAsJsonAsync($"/api/workflows/instances/{record["id"]}/retry", new { })).StatusCode);
    }
}
