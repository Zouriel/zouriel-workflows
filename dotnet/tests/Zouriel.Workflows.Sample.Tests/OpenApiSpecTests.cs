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

    private static IEnumerable<(string Op, JsonObject Operation)> WorkflowOperations(JsonNode doc) =>
        doc["paths"]!.AsObject()
            .Where(p => p.Key.StartsWith("/api/workflows", StringComparison.Ordinal))
            .SelectMany(p => p.Value!.AsObject().Select(op => ($"{op.Key.ToUpperInvariant()} {p.Key}", op.Value!.AsObject())));

    /// <summary>What a client generator sees of an operation: its request body and each response with its content types and whether they carry a schema.</summary>
    private static string Shape(JsonObject op) =>
        $"body={op["requestBody"]?["content"]?.AsObject().Select(c => c.Key).FirstOrDefault() ?? "-"} " + string.Join(" ",
            op["responses"]!.AsObject().OrderBy(r => r.Key).Select(r => $"{r.Key}:" + string.Join(",",
                (r.Value!["content"]?.AsObject() ?? []).Select(c => c.Key + (c.Value!["schema"] is null ? "" : "+schema")))));

    [Fact]
    public async Task The_exported_spec_matches_the_live_request_and_response_shapes()
    {
        var live = WorkflowOperations(JsonNode.Parse(await _app.CreateClient().GetStringAsync("/openapi/v1.json"))!).ToDictionary(x => x.Op, x => Shape(x.Operation));
        var spec = WorkflowOperations(JsonNode.Parse(await File.ReadAllTextAsync(SpecFile()))!).ToDictionary(x => x.Op, x => Shape(x.Operation));
        Assert.All(live, kv => Assert.Equal($"{kv.Key} {kv.Value}", $"{kv.Key} {spec.GetValueOrDefault(kv.Key)}"));
    }

    [Fact]
    public async Task Every_documented_success_response_is_typed_and_deletes_answer_204()
    {
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(SpecFile()))!;
        foreach (var (op, operation) in WorkflowOperations(doc))
        {
            var responses = operation["responses"]!.AsObject();
            var success = responses.Where(r => r.Key.StartsWith('2')).ToList();
            Assert.True(success.Count > 0, $"{op} documents no success response");
            if (op.StartsWith("DELETE ", StringComparison.Ordinal))
            {
                Assert.Equal(["204"], success.Select(r => r.Key));
                continue;
            }
            foreach (var (code, response) in success)
            {
                if (code == "204") continue;
                var content = response!["content"]?.AsObject();
                Assert.True(content is { Count: > 0 }, $"{op} {code} has no content");
                Assert.True(content!.ContainsKey("application/json"), $"{op} {code} is not JSON");
                Assert.All(content, c => Assert.True(c.Value!["schema"] is not null, $"{op} {code} {c.Key} has no schema"));
            }
        }
    }

    [Theory]
    [InlineData("POST /api/workflows/definitions")]
    [InlineData("PUT /api/workflows/definitions/{id}")]
    [InlineData("POST /api/workflows/validate")]
    [InlineData("POST /api/workflows/simulate")]
    [InlineData("POST /api/workflows/tasks/{id}/decide")]
    [InlineData("PUT /api/workflows/settings")]
    [InlineData("POST /api/workflows/records/{entity}/{id}/transitions/{key}")]
    [InlineData("POST /api/workflows/signals/{key}")]
    public async Task Endpoints_that_read_a_body_document_it(string op)
    {
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(SpecFile()))!;
        var operation = WorkflowOperations(doc).Single(x => x.Op == op).Operation;
        Assert.NotNull(operation["requestBody"]?["content"]?["application/json"]?["schema"]);
    }
}
