using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>
/// spec/api/openapi.json is exported from this app. If an endpoint is added, removed or changes its
/// method, this fails until the spec is exported again (see the file's description), so other engines
/// always have the real API to follow.
/// </summary>
public sealed class OpenApiSpecTests : IClassFixture<OpenApiSpecTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Database", Path.Combine(Path.GetTempPath(), $"wf-openapi-{Guid.NewGuid():N}.db"));
    }

    private readonly App _app;

    public OpenApiSpecTests(App app) => _app = app;

    private static HashSet<string> Operations(JsonNode doc) =>
        doc["paths"]!.AsObject()
            .Where(p => p.Key.StartsWith("/api/workflows", StringComparison.Ordinal))
            .SelectMany(p => p.Value!.AsObject().Select(op => $"{op.Key.ToUpperInvariant()} {p.Key}"))
            .ToHashSet();

    private static string SpecFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, "spec", "api", "openapi.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("spec/api/openapi.json not found above the test folder.");
    }

    [Fact]
    public async Task The_exported_spec_lists_exactly_the_endpoints_the_api_serves()
    {
        var live = Operations(JsonNode.Parse(await _app.CreateClient().GetStringAsync("/openapi/v1.json"))!);
        var spec = Operations(JsonNode.Parse(await File.ReadAllTextAsync(SpecFile()))!);
        Assert.NotEmpty(live);
        Assert.Empty(live.Except(spec));
        Assert.Empty(spec.Except(live));
    }
}
