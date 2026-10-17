using Zouriel.Workflows.Testing;

namespace Zouriel.Workflows.Tests;

/// <summary>Runs every engine conformance fixture in <c>spec/fixtures/engine</c>.</summary>
public sealed class EngineFixtureTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(SpecPaths.Fixtures("engine"), "*.json").Where(f => !Path.GetFileName(f).StartsWith('_')).OrderBy(f => f))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public Task Fixture(string file) => ConformanceRunner.RunAsync(Path.Combine(SpecPaths.Fixtures("engine"), file));
}
