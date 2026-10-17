using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>
/// The engine's behaviour changes seen through the REST API: <c>{ initiator: true }</c> is the record's initiator, never
/// the caller; settings are validated on save (400 <c>invalidSettings</c> with every problem); a permissions change
/// reports <c>recordsKept</c>. Uses its own database because it changes the lifecycle and the settings.
/// </summary>
public sealed class IntegrationApiTests : IClassFixture<ApiTests.App>
{
    private readonly ApiTests.App _app;

    public IntegrationApiTests(ApiTests.App app) => _app = app;

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

    private static List<string> Keys(JsonNode record) => record["transitions"]!.AsArray().Select(t => t!["key"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task Only_the_records_initiator_matches_initiator_true_never_the_caller()
    {
        var changed = await Json(await As("hana").PostAsJsonAsync("/api/workflows/definitions/wf_scholarship/permissions", new
        {
            changes = new[] { new { node = "tr_withdraw", who = new JsonArray(new JsonObject { ["initiator"] = true }) } },
            note = "Only the applicant who started it may withdraw",
        }));
        Assert.True(changed["changed"]!.GetValue<bool>());
        Assert.Equal(0, changed["recordsKept"]!.GetValue<int>());

        var created = await Json(await As("sara").PostAsJsonAsync("/api/demo/applications", new { title = "Initiator grant", amount = 500, programmeId = 1 }));
        var id = created["id"]!.GetValue<int>();
        await Task.Delay(1500); // the worker adopts the new record
        await Json(await As("sara").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { }));

        // Before, "the initiator" resolved to whoever asked, so everyone could withdraw. Now it is sara's alone.
        foreach (var other in new[] { "omar", "hana", "ali" })
        {
            Assert.DoesNotContain("withdraw", Keys(await Json(await As(other).GetAsync($"/api/workflows/records/application/{id}"))));
            var refused = await As(other).PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/withdraw", new { });
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{other}: {(int)refused.StatusCode} {await refused.Content.ReadAsStringAsync()}");
        }
        Assert.Contains("withdraw", Keys(await Json(await As("sara").GetAsync($"/api/workflows/records/application/{id}"))));
        var withdrawn = await Json(await As("sara").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/withdraw", new { }));
        Assert.Equal("Withdrawn", withdrawn["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_are_validated_on_save_and_the_removed_fields_are_gone()
    {
        var current = await Json(await As("hana").GetAsync("/api/workflows/settings"));
        var settings = current["settings"]!.AsObject();
        var revision = current["revision"]!.GetValue<int>();
        Assert.False(settings["defaults"]!.AsObject().ContainsKey("taskDue")); // the seed no longer sets it

        var bad = settings.DeepClone().AsObject();
        bad["calendar"]!["hours"] = new JsonArray("9am", "17:00");
        bad["calendar"]!["holidays"] = new JsonArray("2026-02-30");
        bad["timeZone"] = "Mars/Olympus";
        var refused = await As("hana").PutAsJsonAsync("/api/workflows/settings", new { settings = bad, revision });
        var error = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalidSettings", error["code"]!.GetValue<string>());
        Assert.Equal(3, error["details"]!.AsArray().Count); // every problem, not just the first
        Assert.Equal(revision, (await Json(await As("hana").GetAsync("/api/workflows/settings")))["revision"]!.GetValue<int>());

        // Fields the engine never used are not kept, even when a client still sends them.
        var good = settings.DeepClone().AsObject();
        good["defaults"]!["taskDue"] = "P3D";
        good["people"]!["escalationDefault"] = "P2D";
        good["channels"]!["email"]!["sender"] = "office@example.com";
        var saved = await Json(await As("hana").PutAsJsonAsync("/api/workflows/settings", new { settings = good, revision }));
        Assert.Equal(revision + 1, saved["revision"]!.GetValue<int>());
        var after = (await Json(await As("hana").GetAsync("/api/workflows/settings")))["settings"]!.ToJsonString();
        Assert.DoesNotContain("taskDue", after);
        Assert.DoesNotContain("escalationDefault", after);
        Assert.DoesNotContain("sender", after);
    }
}
