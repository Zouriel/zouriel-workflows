namespace Zouriel.Workflows.Time;

/// <summary>A clock that only moves when told to. Used by test runs and conformance tests.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>The current manual time.</summary>
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => _now += by;

    /// <summary>Sets the clock to an instant.</summary>
    public void Set(DateTimeOffset to) => _now = to;
}
