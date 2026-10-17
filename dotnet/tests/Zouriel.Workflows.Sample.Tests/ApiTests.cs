using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>The REST API end to end, through the sample app (SQLite, demo sign-in).</summary>
public sealed class ApiTests : IClassFixture<ApiTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        public string Db { get; } = Path.Combine(Path.GetTempPath(), $"wf-api-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Database", Db).UseSetting("Playground:Enabled", "false");
    }

    private readonly App _app;

    public ApiTests(App app) => _app = app;

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

    private async Task SettleAsync()
    {
        // The worker runs every 500ms; give it a moment.
        await Task.Delay(1500);
    }

    [Fact]
    public async Task Schema_lists_the_registry_and_step_types()
    {
        var schema = await Json(await As("hana").GetAsync("/api/workflows/schema"));
        var app = schema["registry"]!["entities"]!.AsArray().First(e => e!["key"]!.GetValue<string>() == "application")!;
        var status = app["fields"]!.AsArray().First(f => f!["key"]!.GetValue<string>() == "status")!;
        Assert.True(status["lifecycle"]!.GetValue<bool>());
        Assert.Contains(status["options"]!.AsArray(), o => o!["label"]!.GetValue<string>() == "Under review");
        Assert.Contains(schema["steps"]!.AsArray(), s => s!["type"]!.GetValue<string>() == "approval");
    }

    [Fact]
    public async Task Watching_summaries_use_names_not_keys()
    {
        var triggers = (await Json(await As("hana").GetAsync("/api/workflows/triggers"))).AsArray().Select(t => t!["summary"]!.GetValue<string>()).ToList();
        Assert.Contains(triggers, s => s.StartsWith("When Scholarship review enters Submitted", StringComparison.Ordinal));
        Assert.Contains(triggers, s => s.StartsWith("2 days before Application Deadline", StringComparison.Ordinal));
        Assert.DoesNotContain(triggers, s => s.Contains("scholarshipReview", StringComparison.Ordinal) || s.Contains("-P", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Me_says_what_the_caller_may_do()
    {
        var hana = await Json(await As("hana").GetAsync("/api/workflows/me"));
        Assert.Equal("hana", hana["id"]!.GetValue<string>());
        Assert.True(hana["can"]!["edit"]!.GetValue<bool>());
        Assert.True(hana["can"]!["settings"]!.GetValue<bool>());
        var ali = await Json(await As("ali").GetAsync("/api/workflows/me"));
        Assert.False(ali["can"]!["edit"]!.GetValue<bool>());
        Assert.False(ali["can"]!["intervene"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Editing_needs_the_admin_policy_and_drafts_are_validated()
    {
        var bad = new JsonObject { ["specVersion"] = 1, ["id"] = "wf_x", ["key"] = "x", ["kind"] = "automation", ["title"] = "X", ["entity"] = "nope", ["steps"] = new JsonArray() };
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").PostAsJsonAsync("/api/workflows/definitions", bad)).StatusCode);
        var problems = await Json(await As("hana").PostAsJsonAsync("/api/workflows/validate", bad));
        Assert.Contains(problems.AsArray(), p => p!["code"]!.GetValue<string>() == "unknownEntity");
        Assert.Contains(problems.AsArray(), p => p!["code"]!.GetValue<string>() == "missingTrigger");
        await Json(await As("hana").PostAsJsonAsync("/api/workflows/definitions", bad));
        var publish = await As("hana").PostAsJsonAsync("/api/workflows/definitions/wf_x/publish", new { note = "try" });
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        Assert.Equal("lintErrors", JsonNode.Parse(await publish.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await As("hana").DeleteAsync("/api/workflows/definitions/wf_x")).StatusCode);
    }

    [Fact]
    public async Task A_record_moves_through_its_lifecycle_and_starts_the_finance_automation()
    {
        var created = await Json(await As("sara").PostAsJsonAsync("/api/demo/applications", new { title = "Test grant", amount = 900, programmeId = 1 }));
        var id = created["id"]!.GetValue<int>();
        await SettleAsync();
        var record = await Json(await As("sara").GetAsync($"/api/workflows/records/application/{id}"));
        Assert.Equal("Draft", record["state"]!.GetValue<string>());
        Assert.Contains(record["transitions"]!.AsArray(), t => t!["key"]!.GetValue<string>() == "submit");

        var refused = await As("sara").PatchAsJsonAsync($"/api/demo/applications/{id}", new { status = "Approved" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("omar").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { })).StatusCode);
        await Json(await As("sara").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { }));
        await Json(await As("omar").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/startReview", new { }));
        var invalid = await As("omar").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/approve", new { input = new { comment = "ok" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var approved = await Json(await As("omar").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/approve", new { input = new { comment = "Good to go, approved." } }));
        Assert.Equal("Approved", approved["state"]!.GetValue<string>());
        await SettleAsync();

        var tasks = await Json(await As("farah").GetAsync("/api/workflows/tasks"));
        var task = tasks["items"]!.AsArray().First(t => t!["entityId"]!.GetValue<string>() == id.ToString())!;
        Assert.True(task["canDecide"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await As("sara").PostAsJsonAsync($"/api/workflows/tasks/{task["id"]}/decide", new { outcome = "approved" })).StatusCode);
        await Json(await As("farah").PostAsJsonAsync($"/api/workflows/tasks/{task["id"]}/decide", new { outcome = "approved" }));
        await SettleAsync();
        await Json(await As("farah").PostAsync($"/api/demo/applications/{id}/payment", null));
        await SettleAsync();
        var app = await Json(await As("sara").GetAsync($"/api/demo/applications/{id}"));
        Assert.True(app["paid"]!.GetValue<bool>());

        var verify = await Json(await As("hana").PostAsync("/api/workflows/audit/verify", null));
        Assert.True(verify["valid"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await As("sara").GetAsync("/api/workflows/audit")).StatusCode);
        var csv = await As("hana").GetAsync("/api/workflows/audit/export?format=csv&entity=application&entityId=" + id);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("lifecycle", await csv.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_reset_list_is_only_for_the_reset_s_people_and_a_list_that_changed_since_is_refused()
    {
        var created = await Json(await As("sara").PostAsJsonAsync("/api/demo/applications", new { title = "Reset grant", amount = 700, programmeId = 1 }));
        var id = created["id"]!.GetValue<int>();
        await SettleAsync();
        await Json(await As("sara").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { }));
        await Json(await As("omar").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/startReview", new { }));
        await SettleAsync();
        var path = $"/api/workflows/records/application/{id}/reset";
        var reviewer = $"field:application:{id}:reviewer";

        Assert.Equal(HttpStatusCode.Forbidden, (await As("sara").GetAsync(path)).StatusCode);
        var list = await Json(await As("hana").GetAsync(path));
        Assert.Equal("Start over", list["reset"]!["label"]!.GetValue<string>());
        var item = list["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == reviewer)!;
        Assert.True(item["selected"]!.GetValue<bool>());
        var token = list["token"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(token));

        // Someone picks another reviewer after the list was opened: the old list no longer holds.
        await Json(await As("hana").PatchAsJsonAsync($"/api/demo/applications/{id}", new { reviewer = "rana" }));
        await SettleAsync();
        var reason = new { reason = "The wrong programme was chosen." };
        var stale = await As("hana").PostAsJsonAsync(path, new { input = reason, undo = new[] { reviewer }, token });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resetChanged", JsonNode.Parse(await stale.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());

        var fresh = (await Json(await As("hana").GetAsync(path)))["token"]!.GetValue<string>();
        Assert.NotEqual(token, fresh);
        var noReason = await As("hana").PostAsJsonAsync(path, new { undo = new[] { reviewer }, token = fresh });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal("invalidInput", JsonNode.Parse(await noReason.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());

        var done = await Json(await As("hana").PostAsJsonAsync(path, new { input = reason, undo = new[] { reviewer }, token = fresh }));
        Assert.Equal("Draft", done["state"]!.GetValue<string>());
        var app = await Json(await As("hana").GetAsync($"/api/demo/applications/{id}"));
        Assert.Null(app["reviewer"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("Invoice INV-00042 for 1500.5.", "")]
    [InlineData("Keep this. Invoice INV-00042 for 1500.5. And this.", "Keep this. And this.")]
    [InlineData("Invoice INV-00042 for 900. Invoice INV-00043 for 12.25.", "Invoice INV-00043 for 12.25.")]
    [InlineData("Invoice INV-000420 for 1.", "Invoice INV-000420 for 1.")]
    public void Undoing_an_invoice_removes_exactly_its_sentence(string notes, string expected) =>
        Assert.Equal(expected, CreateInvoice.WithoutInvoice(notes, "INV-00042"));

    [Fact]
    public async Task Nothing_is_paid_when_the_sign_off_runs_out_of_time()
    {
        var def = JsonNode.Parse((await (await As("hana").GetAsync("/api/workflows/definitions/wf_finance")).Content.ReadAsStringAsync()))!["draft"]!;
        var result = await Json(await As("hana").PostAsJsonAsync("/api/workflows/simulate", new
        {
            definition = def,
            request = new { entityId = "2", events = new object[] { new { type = "advance", duration = "P10D" } } },
        }));
        var log = result["log"]!.AsArray().Select(l => $"{l!["kind"]}.{l["action"]}").ToList();
        Assert.DoesNotContain("action.finance.createInvoice", log);
        var run = result["instances"]!.AsArray().Single(i => i!["definitionKey"]!.GetValue<string>() == "financeSignOff")!;
        Assert.Equal("failed", run["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_test_run_reports_what_would_happen_without_doing_it()
    {
        var def = JsonNode.Parse((await (await As("hana").GetAsync("/api/workflows/definitions/wf_finance")).Content.ReadAsStringAsync()))!["draft"]!;
        var result = await Json(await As("hana").PostAsJsonAsync("/api/workflows/simulate", new
        {
            definition = def,
            request = new { entityId = "2", events = new object[] { new { type = "decide", step = "s_fin", outcome = "approved" }, new { type = "decide", step = "s_head", outcome = "approved" } } },
        }));
        Assert.Null(result["error"]?.GetValue<string>());
        var log = result["log"]!.AsArray().Select(l => $"{l!["kind"]}.{l["action"]}").ToList();
        Assert.Contains("action.finance.createInvoice", log);
        Assert.Contains(log, l => l.StartsWith("notify.", StringComparison.Ordinal));
        // Nothing real happened: the record is still unpaid and no real tasks exist for it.
        var app = await Json(await As("hana").GetAsync("/api/demo/applications/2"));
        Assert.False(app["paid"]!.GetValue<bool>());
    }
}
