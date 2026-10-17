using System.Text.Json;
using System.Text.Json.Nodes;
using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Tests;

/// <summary>
/// The form builder's own condition and calculation fixtures (copied from
/// <c>@zouriel/ui/form-builder/src/fixtures</c>): workflows reuse the same rule language, so the .NET
/// evaluator must agree with the TypeScript reference case for case.
/// </summary>
public sealed class ExpressionFixtureTests
{
    private static readonly HashSet<string> FormOnly = ["visible", "hidden", "complete", "incomplete", "rowsGte", "rowsLte"];

    public static TheoryData<string> ConditionCases() => Cases("conditions.json");
    public static TheoryData<string> CalcCases() => Cases("calc.json");

    private static TheoryData<string> Cases(string file)
    {
        var data = new TheoryData<string>();
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(SpecPaths.Fixtures("conditions"), file)))!;
        foreach (var c in doc["cases"]!.AsArray()) data.Add(c!["name"]!.GetValue<string>());
        return data;
    }

    private static (JsonObject Case, string Today) Load(string file, string name)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(SpecPaths.Fixtures("conditions"), file)))!;
        var c = doc["cases"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == name)!.AsObject();
        return (c, doc["today"]!.GetValue<string>());
    }

    private sealed class Ctx(Dictionary<string, object?> values, string today) : IValueContext
    {
        public object? Read(string path) => values.GetValueOrDefault(path);
        public string Today { get; } = today;
    }

    private static bool UsesFormOnly(JsonNode? c) => c switch
    {
        JsonObject o when o["conditions"] is JsonArray a => a.Any(UsesFormOnly),
        JsonObject o => FormOnly.Contains(o["op"]?.GetValue<string>() ?? ""),
        _ => false,
    };

    [Theory]
    [MemberData(nameof(ConditionCases))]
    public void Condition(string name)
    {
        var (c, today) = Load("conditions.json", name);
        if (UsesFormOnly(c["condition"])) return; // visible/complete/rows only exist in forms
        var cond = c["condition"] is null ? null : c["condition"]!.Deserialize<Condition>(Definitions.JsonOptions);
        var values = Values.FromJson(c["values"]) as Dictionary<string, object?> ?? [];
        Assert.Equal(c["expected"]!.GetValue<bool>(), Conditions.Evaluate(cond, new Ctx(values, today)));
    }

    private sealed class Source(Dictionary<string, object?> values, Dictionary<string, object?> rows, string today) : Calculations.ISource
    {
        public IReadOnlyList<object?> Values(string path) => values.GetValueOrDefault(path) as List<object?> ?? [];
        public int? Rows(string path) => rows.GetValueOrDefault(path) is double d ? (int)d : null;
        public string Today { get; } = today;
    }

    [Theory]
    [MemberData(nameof(CalcCases))]
    public void Calc(string name)
    {
        var (c, today) = Load("calc.json", name);
        var calc = c["calc"]!.Deserialize<Calc>(Definitions.JsonOptions);
        var values = Values.FromJson(c["values"]) as Dictionary<string, object?> ?? [];
        var rows = Values.FromJson(c["rows"]) as Dictionary<string, object?> ?? [];
        var result = Calculations.Evaluate(calc, new Source(values, rows, today));
        var expected = Values.FromJson(c["expected"]);
        Assert.True(Values.DeepEquals(expected, result), $"expected {Values.Serialize(expected)}, got {Values.Serialize(result)}");
    }
}
