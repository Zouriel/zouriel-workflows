namespace Zouriel.Workflows.Time;

/// <summary>A clock that only moves when told to. Used by test runs and conformance tests.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
