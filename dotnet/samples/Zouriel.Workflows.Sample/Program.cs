using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Zouriel.Workflows.AspNetCore;
using Zouriel.Workflows.Sample;

var builder = WebApplication.CreateBuilder(args);
var playground = builder.Configuration.GetValue<bool>("Playground:Enabled");

// ── Workflows ───────────────────────────────────────────────────────────
// A normal app: one SQLite database for records and workflows (see SampleSetup.AddSampleApp).
// The playground: the same app once per visitor, each in its own sandbox (see SandboxManager), where
// webhooks are recorded instead of sent.
builder.Services.AddSampleApp(SampleSetup.FileDatabase(builder.Configuration["Database"] ?? "sample.db"), TimeProvider.System, runWorker: !playground,
    webhooks: playground ? new PlaygroundWebhooks() : null);
if (playground)
{
    builder.Services.AddSingleton<SandboxManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SandboxManager>());
}

// ── Demo sign-in, CORS and limits ───────────────────────────────────────
// Demo sign-in and policies come with AddSampleApp (SampleSetup.AddSampleSecurity).
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4310", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));

// Behind a reverse proxy the connection comes from the proxy; X-Forwarded-For carries the visitor. Only
// proxies listed in ForwardedHeaders:KnownProxies, or inside ForwardedHeaders:KnownNetworks (CIDR, for a
// proxy whose container address changes), are believed (default: this machine), so clients cannot pick
// their own address to dodge the rate limits below.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var ip in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        if (IPAddress.TryParse(ip, out var address)) o.KnownProxies.Add(address);
    foreach (var cidr in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        if (System.Net.IPNetwork.TryParse(cidr, out var network)) o.KnownIPNetworks.Add(network);
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
    // New playground sessions are expensive (a database and a seed run each): a few per visitor per hour.
    o.AddPolicy(DemoEndpoints.SessionRateLimit, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromHours(1) }));
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

var app = builder.Build();
app.UseForwardedHeaders();
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();

if (playground) app.Use(RunInSandboxAsync);
else await SampleSetup.SeedAsync(app.Services);

app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
app.MapWorkflowApi("/api/workflows", o =>
{
    o.EditPolicy = "workflows.edit";
    o.PublishPolicy = "workflows.edit";
    o.SettingsPolicy = "workflows.settings";
    o.InterventionPolicy = "workflows.intervene";
    o.AuditPolicy = "workflows.audit";
});
app.MapDemoEndpoints();
if (playground) app.MapPlaygroundEndpoints();

app.Run();

static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

// Each playground request runs inside the visitor's sandbox: its own DI container, database and clock.
static async Task RunInSandboxAsync(HttpContext ctx, Func<Task> next)
{
    var path = ctx.Request.Path;
    if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health") || path.StartsWithSegments(DemoEndpoints.SessionPath))
    {
        await next();
        return;
    }
    var boxes = ctx.RequestServices.GetRequiredService<SandboxManager>();
    var id = ctx.Request.Headers["X-Playground-Session"].FirstOrDefault() ?? ctx.Request.Query["session"].FirstOrDefault();
    if (!boxes.IsIssued(id))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { code = "noSession", message = $"Get a session with POST {DemoEndpoints.SessionPath} and send it as the X-Playground-Session header." });
        return;
    }
    Sandbox box;
    try
    {
        box = await boxes.GetAsync(id!, ctx.RequestAborted);
    }
    catch (PlaygroundFullException e)
    {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsJsonAsync(new { code = "busy", message = e.Message });
        return;
    }
    await using var scope = box.Services.CreateAsyncScope();
    ctx.RequestServices = scope.ServiceProvider;
    ctx.Items["sandbox"] = box;
    await next();
}

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
