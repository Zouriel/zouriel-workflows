namespace Zouriel.Workflows.Engine;

/// <summary>One thing that happened (or would have happened) during a test run.</summary>
public sealed record SimulationEntry(string Kind, string Action, string? Subject, string? Text, object? Data, string At);

/// <summary>Collects what a test run did, including side effects that were skipped.</summary>
public sealed class SimulationLog(Func<DateTimeOffset> now)
{
    private readonly List<SimulationEntry> _entries = [];

    public IReadOnlyList<SimulationEntry> Entries => _entries;

    public void Add(string kind, string action, string? subject, string? text, object? data = null) =>
        _entries.Add(new SimulationEntry(kind, action, subject, text, Json.Values.Normalize(data), Json.Values.FormatInstant(now())));
}
