namespace Zouriel.Workflows.Tests;

internal static class SpecPaths
{
    /// <summary>The repository's <c>spec/</c> folder, found by walking up from the test binaries.</summary>
    public static string Spec
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "spec", "fixtures"))) dir = dir.Parent;
            return dir is null ? throw new DirectoryNotFoundException("spec/fixtures not found above the test folder.") : Path.Combine(dir.FullName, "spec");
        }
    }

    public static string Fixtures(string kind) => Path.Combine(Spec, "fixtures", kind);
}
