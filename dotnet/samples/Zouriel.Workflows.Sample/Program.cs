using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Zouriel.Workflows.AspNetCore;
using Zouriel.Workflows.Sample;

var builder = WebApplication.CreateBuilder(args);
var playground = builder.Configuration.GetValue<bool>("Playground:Enabled");

// ── Workflows ───────────────────────────────────────────────────────────
// A normal app: one SQLite database for records and workflows (see SampleSetup.AddSampleApp).
// The playground: the same app once per visitor, each in its own sandbox (see SandboxManager).
builder.Services.AddSampleApp(SampleSetup.FileDatabase(builder.Configuration["Database"] ?? "sample.db"), TimeProvider.System, runWorker: !playground);
if (playground)
{
    builder.Services.AddSingleton<SandboxManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SandboxManager>());
}

// ── Demo sign-in, CORS and limits ───────────────────────────────────────
builder.Services.AddAuthentication(DemoAuthHandler.SchemeName).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoAuthHandler>(DemoAuthHandler.SchemeName, null);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("workflows.edit", p => p.RequireRole("admin", "head"));
    o.AddPolicy("workflows.intervene", p => p.RequireRole("admin", "head"));
    o.AddPolicy("workflows.audit", p => p.RequireRole("admin", "head"));
});
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4310", "http://localhost:4200"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

var app = builder.Build();
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();

if (playground)
{
    // Each request runs inside the visitor's sandbox: its own DI container, database and clock.
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/health"))
        {
            await next();
            return;
        }
        var id = ctx.Request.Headers["X-Playground-Session"].FirstOrDefault() ?? ctx.Request.Query["session"].FirstOrDefault();
        if (id is null || !SandboxManager.ValidId().IsMatch(id))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { code = "noSession", message = "Send an X-Playground-Session header (8–64 letters, digits or dashes)." });
            return;
        }
        var box = await ctx.RequestServices.GetRequiredService<SandboxManager>().GetAsync(id, ctx.RequestAborted);
        await using var scope = box.Services.CreateAsyncScope();
        ctx.RequestServices = scope.ServiceProvider;
        ctx.Items["sandbox"] = box;
        await next();
    });
}
else
{
    await SampleSetup.SeedAsync(app.Services);
}

app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
app.MapWorkflowApi("/api/workflows", o =>
{
    o.EditPolicy = "workflows.edit";
    o.PublishPolicy = "workflows.edit";
    o.SettingsPolicy = "workflows.edit";
    o.InterventionPolicy = "workflows.intervene";
    o.AuditPolicy = "workflows.audit";
});
app.MapDemoEndpoints();
if (playground) app.MapPlaygroundEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
