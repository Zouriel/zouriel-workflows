using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    private async Task<string> NewSessionAsync()
    {
        var r = await _app.CreateClient().PostAsync("/api/playground/session", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!["session"]!.GetValue<string>();
    }

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
        var idA = await NewSessionAsync();
        var a = Session(idA);
        var b = Session(await NewSessionAsync());
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/demo/applications", new { title = "Only in A", amount = 10 })).StatusCode);
        var inA = JsonNode.Parse(await a.GetStringAsync("/api/demo/applications"))!.AsArray();
        var inB = JsonNode.Parse(await b.GetStringAsync("/api/demo/applications"))!.AsArray();
        Assert.Contains(inA, x => x!["title"]!.GetValue<string>() == "Only in A");
        Assert.DoesNotContain(inB, x => x!["title"]!.GetValue<string>() == "Only in A");
        await a.PostAsync("/api/playground/reset", null);
        var fresh = JsonNode.Parse(await Session(idA).GetStringAsync("/api/demo/applications"))!.AsArray();
        Assert.DoesNotContain(fresh, x => x!["title"]!.GetValue<string>() == "Only in A");
    }

    [Fact]
    public async Task Requests_without_a_session_are_refused_and_the_clock_can_move()
    {
        var anon = _app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/api/demo/applications")).StatusCode);
        var c = Session(await NewSessionAsync());
        var clock = await c.GetAsync("/api/demo/clock");
        var clockText = await clock.Content.ReadAsStringAsync();
        Assert.True(clock.IsSuccessStatusCode, clockText);
        var before = JsonNode.Parse(clockText)!["now"]!.GetValue<DateTimeOffset>();
        var moved = JsonNode.Parse(await (await c.PostAsJsonAsync("/api/playground/advance", new { duration = "P2D" })).Content.ReadAsStringAsync())!;
        Assert.True(moved["now"]!.GetValue<DateTimeOffset>() - before > TimeSpan.FromDays(1.9));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/playground/advance", new { duration = "P1Y" })).StatusCode);
    }

    [Fact]
    public async Task Session_ids_the_server_did_not_issue_are_refused()
    {
        // A chosen id (the old scheme), and an issued id with its signature changed.
        Assert.Equal(HttpStatusCode.BadRequest, (await Session("session-aaaaaaaa").GetAsync("/api/demo/applications")).StatusCode);
        var issued = await NewSessionAsync();
        var forged = issued[..^2] + (issued[^2..] == "AA" ? "BB" : "AA");
        Assert.Equal(HttpStatusCode.BadRequest, (await Session(forged).GetAsync("/api/demo/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Session(issued).GetAsync("/api/demo/applications")).StatusCode);
    }

    [Fact]
    public async Task Sandboxes_never_send_real_webhooks()
    {
        var boxes = _app.Services.GetRequiredService<SandboxManager>();
        var box = await boxes.GetAsync(await NewSessionAsync(), CancellationToken.None);
        Assert.IsType<PlaygroundWebhooks>(box.Services.GetRequiredService<IWebhookClient>());
        Assert.IsType<PlaygroundWebhooks>(_app.Services.GetRequiredService<IWebhookClient>());
    }

    [Fact]
    public async Task The_workflow_api_needs_a_signed_in_user_in_the_playground_too()
    {
        var session = await NewSessionAsync();
        var anon = _app.CreateClient();
        anon.DefaultRequestHeaders.Add("X-Playground-Session", session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/workflows/definitions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Session(session, "hana").GetAsync("/api/workflows/tasks?scope=all")).StatusCode);
    }
}
