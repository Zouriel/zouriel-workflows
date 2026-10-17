using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>Playground mode: every session gets its own sandbox (database, workflows, clock).</summary>
public sealed class PlaygroundTests : IClassFixture<PlaygroundTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("Playground:Enabled", "true")
            .UseSetting("Playground:Directory", Path.Combine(Path.GetTempPath(), $"wf-pg-{Guid.NewGuid():N}"))
            .UseSetting("Database", Path.Combine(Path.GetTempPath(), $"wf-pg-root-{Guid.NewGuid():N}.db"));
    }

    private readonly App _app;

    public PlaygroundTests(App app) => _app = app;

    private HttpClient Session(string session, string user = "ali")
    {
        var c = _app.CreateClient();
        c.DefaultRequestHeaders.Add("X-Playground-Session", session);
        c.DefaultRequestHeaders.Add("X-Demo-User", user);
        return c;
    }

    [Fact]
    public async Task Sessions_are_isolated_and_can_be_reset()
    {
        var a = Session("session-aaaaaaaa");
        var b = Session("session-bbbbbbbb");
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/demo/applications", new { title = "Only in A", amount = 10 })).StatusCode);
        var inA = JsonNode.Parse(await a.GetStringAsync("/api/demo/applications"))!.AsArray();
        var inB = JsonNode.Parse(await b.GetStringAsync("/api/demo/applications"))!.AsArray();
        Assert.Contains(inA, x => x!["title"]!.GetValue<string>() == "Only in A");
        Assert.DoesNotContain(inB, x => x!["title"]!.GetValue<string>() == "Only in A");
        await a.PostAsync("/api/playground/reset", null);
        var fresh = JsonNode.Parse(await Session("session-aaaaaaaa").GetStringAsync("/api/demo/applications"))!.AsArray();
        Assert.DoesNotContain(fresh, x => x!["title"]!.GetValue<string>() == "Only in A");
    }

    [Fact]
    public async Task Requests_without_a_session_are_refused_and_the_clock_can_move()
    {
        var anon = _app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/api/demo/applications")).StatusCode);
        var c = Session("session-clock-1");
        var clock = await c.GetAsync("/api/demo/clock");
        var clockText = await clock.Content.ReadAsStringAsync();
        Assert.True(clock.IsSuccessStatusCode, clockText);
        var before = JsonNode.Parse(clockText)!["now"]!.GetValue<DateTimeOffset>();
        var moved = JsonNode.Parse(await (await c.PostAsJsonAsync("/api/playground/advance", new { duration = "P2D" })).Content.ReadAsStringAsync())!;
        Assert.True(moved["now"]!.GetValue<DateTimeOffset>() - before > TimeSpan.FromDays(1.9));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/playground/advance", new { duration = "P1Y" })).StatusCode);
    }
}
