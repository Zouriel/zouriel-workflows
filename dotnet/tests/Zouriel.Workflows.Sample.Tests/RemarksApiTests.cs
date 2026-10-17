using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>
/// Remarks on moves and resets through the REST API (spec §6): the record view says whether each move asks
/// for them, the transition and reset bodies carry <c>comment</c>, missing required remarks are a 400
/// <c>commentRequired</c>, overlong ones a 400 <c>invalidInput</c>, and the audit keeps them. Uses its own
/// database because it publishes a changed lifecycle.
/// </summary>
public sealed class RemarksApiTests : IClassFixture<ApiTests.App>
{
    private readonly ApiTests.App _app;

    public RemarksApiTests(ApiTests.App app) => _app = app;

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

    private static async Task AssertError(HttpResponseMessage r, string code)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{(int)r.StatusCode}: {text}");
        Assert.Equal(code, JsonNode.Parse(text)!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Remarks_are_offered_required_capped_and_audited_on_moves_and_resets()
    {
        // The demo lifecycle, with remarks required to send an application back and to start it over,
        // and Start over offered only while it is submitted or under review.
        var def = JsonNode.Parse(SampleSetup.ReadSeed("scholarship-lifecycle"))!.AsObject();
        var lc = def["lifecycle"]!.AsObject();
        lc["transitions"]!.AsArray().First(t => t!["key"]!.GetValue<string>() == "sendBack")!["comment"] = "required";
        lc["reset"]!["comment"] = "required";
        lc["reset"]!["states"] = new JsonArray("st_submitted", "st_review");
        var current = await Json(await As("hana").GetAsync("/api/workflows/definitions/wf_scholarship"));
        def["revision"] = current["revision"]?.DeepClone();
        await Json(await As("hana").PutAsJsonAsync("/api/workflows/definitions/wf_scholarship", def));
        await Json(await As("hana").PostAsJsonAsync("/api/workflows/definitions/wf_scholarship/publish", new { note = "remarks" }));

        var created = await Json(await As("sara").PostAsJsonAsync("/api/demo/applications", new { title = "Remarks grant", amount = 500, programmeId = 1 }));
        var id = created["id"]!.GetValue<int>();
        var record = $"/api/workflows/records/application/{id}";
        await Task.Delay(1500);
        Assert.Null((await Json(await As("hana").GetAsync(record)))["reset"]);
        await Json(await As("sara").PostAsJsonAsync($"{record}/transitions/submit", new { comment = "ignored: submit asks for none" }));
        await Json(await As("omar").PostAsJsonAsync($"{record}/transitions/startReview", new { }));

        var view = await Json(await As("omar").GetAsync(record));
        string Remarks(string key) => view["transitions"]!.AsArray().First(t => t!["key"]!.GetValue<string>() == key)!["comment"]!.GetValue<string>();
        Assert.Equal("optional", Remarks("reject"));
        Assert.Equal("none", Remarks("approve"));
        Assert.Equal("required", Remarks("sendBack"));
        Assert.Equal("required", (await Json(await As("hana").GetAsync(record)))["reset"]!["comment"]!.GetValue<string>());

        await AssertError(await As("omar").PostAsJsonAsync($"{record}/transitions/sendBack", new { comment = "  " }), "commentRequired");
        await AssertError(await As("omar").PostAsJsonAsync($"{record}/transitions/sendBack", new { comment = new string('x', 2001) }), "invalidInput");
        await Json(await As("omar").PostAsJsonAsync($"{record}/transitions/sendBack", new { comment = " Add the transcript. " }));

        await Json(await As("sara").PostAsJsonAsync($"{record}/transitions/submit", new { }));
        var reset = $"{record}/reset";
        var reason = new { reason = "Picked the wrong programme" };
        await AssertError(await As("hana").PostAsJsonAsync(reset, new { input = reason }), "commentRequired");
        await Json(await As("hana").PostAsJsonAsync(reset, new { input = reason, comment = "Wrong programme" }));
        // Back in Draft, where Start over is not offered.
        Assert.Null((await Json(await As("hana").GetAsync(record)))["reset"]);
        var refused = await As("hana").PostAsJsonAsync(reset, new { input = reason, comment = "Again" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var audit = await Json(await As("hana").GetAsync($"/api/workflows/audit?entity=application&entityId={id}&take=200"));
        var data = audit.AsArray();
        string? Comment(string action, string? transition = null) => data
            .Where(a => a!["action"]!.GetValue<string>() == action)
            .Select(a => JsonNode.Parse(a!["data"]!.GetValue<string>()))
            .Where(d => transition is null || d?["transition"]?.GetValue<string>() == transition)
            .Select(d => d?["comment"]?.GetValue<string>()).FirstOrDefault();
        Assert.Equal("Add the transcript.", Comment("transitioned", "sendBack"));
        Assert.Null(Comment("transitioned", "submit"));
        Assert.Equal("Wrong programme", Comment("reset"));
    }
}
