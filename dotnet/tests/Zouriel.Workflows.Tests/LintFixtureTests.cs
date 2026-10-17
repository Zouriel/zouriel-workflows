using System.Text.Json.Nodes;
using Zouriel.Workflows.Lint;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;

namespace Zouriel.Workflows.Tests;

/// <summary>Runs <c>spec/fixtures/lint</c>: each definition must produce exactly the listed problem codes.</summary>
public sealed class LintFixtureTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(SpecPaths.Fixtures("lint"), "*.json").OrderBy(f => f)) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Fixture(string file)
    {
        var dir = SpecPaths.Fixtures("lint");
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, file)))!;
        var registry = WorkflowRegistry.FromJson(File.ReadAllText(Path.Combine(dir, fx["registry"]!.GetValue<string>())));
        var settings = GlobalSettings.Parse(fx["settings"]?.ToJsonString());
        var others = (fx["others"] as JsonArray ?? []).Select(o => Definitions.Parse(o!.ToJsonString())).ToList();
        var problems = Linter.Lint(Definitions.Parse(fx["definition"]!.ToJsonString()), new LintContext(registry, settings, others));
        string Key(string code, string? node, string severity) => $"{code}|{node ?? "-"}|{severity}";
        var expected = fx["expected"]!.AsArray().Select(e => Key(e!["code"]!.GetValue<string>(), e["node"]?.GetValue<string>(), e["severity"]?.GetValue<string>() ?? "error"))
            .OrderBy(x => x).ToList();
        // Expected entries without a node match any node.
        var actual = problems.Select(p => Key(p.Code, fx["expected"]!.AsArray().Any(e => e!["code"]!.GetValue<string>() == p.Code && e["node"] is null) ? null : p.Node, p.Severity))
            .OrderBy(x => x).ToList();
        Assert.True(expected.SequenceEqual(actual),
            $"expected:\n  {string.Join("\n  ", expected)}\nactual:\n  {string.Join("\n  ", problems.Select(p => $"{Key(p.Code, p.Node, p.Severity)}  {p.Message}"))}");
    }
}
