using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Engine;

/// <summary>
/// Whether a lifecycle applies to a record (spec §6 applies to). The condition reads only the record's
/// own fields (<c>entity.&lt;field&gt;</c>), so it can be checked while the app is saving, from the values
/// being written, without reading anything else.
/// </summary>
internal static class LifecycleSelection
{
    public static bool Applies(Condition? appliesWhen, IReadOnlyDictionary<string, object?>? values, string today) =>
        appliesWhen is null || Conditions.Evaluate(appliesWhen, new RecordValues(values, today));

    private sealed class RecordValues(IReadOnlyDictionary<string, object?>? values, string today) : IValueContext
    {
        public string Today => today;

        public object? Read(string path) =>
            path.StartsWith("entity.", StringComparison.Ordinal) && values is not null && values.TryGetValue(path[7..], out var v) ? Values.Normalize(v) : null;
    }
}
