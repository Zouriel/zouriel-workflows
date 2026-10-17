using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>The playground's limits: resets count as new sessions, and each sandbox has quotas.</summary>
public sealed class PlaygroundLimitsTests
{
    private sealed class App(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Playground:Enabled", "true")
                .UseSetting("Playground:Directory", Path.Combine(Path.GetTempPath(), $"wf-pgl-{Guid.NewGuid():N}"))
                .UseSetting("Database", Path.Combine(Path.GetTempPath(), $"wf-pgl-root-{Guid.NewGuid():N}.db"));
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
        }
    }

    private static async Task<string> NewSessionAsync(App app)
    {
        var r = await app.CreateClient().PostAsync("/api/playground/session", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!["session"]!.GetValue<string>();
    }

    private static HttpClient Session(App app, string session, string user = "ali")
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("X-Playground-Session", session);
        c.DefaultRequestHeaders.Add("X-Demo-User", user);
        return c;
    }

    [Fact]
    public async Task Resets_count_against_the_session_rate_limit()
    {
        using var app = new App(("Playground:SessionsPerHour", "3"));
        var session = await NewSessionAsync(app);
        for (var i = 0; i < 2; i++)
        {
            var r = await Session(app, session).PostAsync("/api/playground/reset", null);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            session = JsonNode.Parse(await r.Content.ReadAsStringAsync())!["session"]!.GetValue<string>();
        }
        // One session and two resets used the three allowed: a further reset is refused like a new session would be.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Session(app, session).PostAsync("/api/playground/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.CreateClient().PostAsync("/api/playground/session", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Session(app, session).GetAsync("/api/demo/applications")).StatusCode);
    }

    [Fact]
    public async Task A_sandbox_at_its_record_quota_refuses_new_work_until_reset()
    {
        // The seed holds 8 records (2 programmes, 2 applications, 4 documents); each new application adds 3.
        using var app = new App(("Playground:MaxRecords", "12"));
        var session = await NewSessionAsync(app);
        var c = Session(app, session);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/demo/applications", new { title = "One", amount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/demo/applications", new { title = "Two", amount = 1 })).StatusCode);
        var full = await c.PostAsJsonAsync("/api/demo/applications", new { title = "Three", amount = 1 });
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal("quota", JsonNode.Parse(await full.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/demo/applications")).StatusCode);
        var reset = await c.PostAsync("/api/playground/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var fresh = Session(app, JsonNode.Parse(await reset.Content.ReadAsStringAsync())!["session"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsJsonAsync("/api/demo/applications", new { title = "Again", amount = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_sandbox_at_its_instance_quota_refuses_new_work()
    {
        // The seed starts a record instance per application: two already.
        using var app = new App(("Playground:MaxInstances", "2"));
        var session = await NewSessionAsync(app);
        var r = await Session(app, session).PostAsJsonAsync("/api/demo/applications", new { title = "Over", amount = 1 });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        // Workflow API writes too; reads still work.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Session(app, session, "hana").PostAsync("/api/workflows/manual/anything", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Session(app, session, "hana").GetAsync("/api/workflows/definitions")).StatusCode);
    }

    [Fact]
    public async Task Background_work_per_sandbox_is_capped_per_minute()
    {
        using var app = new App(("Playground:MaxBackgroundPerMinute", "1"));
        var session = await NewSessionAsync(app);
        var c = Session(app, session);
        // The first request creates and seeds the sandbox (the seed runs its own events, outside the budget).
        var box = await app.Services.GetRequiredService<SandboxManager>().GetAsync(session, CancellationToken.None);
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/demo/applications", new { title = "A", amount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/demo/applications", new { title = "B", amount = 1 })).StatusCode);
        await c.PostAsync("/api/playground/settle", null);
        await c.PostAsync("/api/playground/settle", null);
        await using var scope = box.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDb>();
        var rows = (await db.Set<OutboxRecord>().AsNoTracking().ToListAsync()).Where(o => o.CreatedAt >= before).ToList();
        // Several created events were waiting; within this minute only one may run.
        Assert.True(rows.Count >= 2, $"{rows.Count} new rows");
        Assert.Equal(1, rows.Count(o => o.ProcessedAt is not null));
    }
}
