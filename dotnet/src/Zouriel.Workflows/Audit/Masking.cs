using Zouriel.Workflows.Json;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;

namespace Zouriel.Workflows.Audit;

/// <summary>
/// Masks sensitive values wherever data leaves the engine: audit entries, run and task views, and test
/// runs (spec §11). A value is masked when it sits under a sensitive key (a registry field marked
/// <c>sensitive</c>, an <c>entity.field</c> or <c>input.key</c> listed in <c>settings.audit.maskedFields</c>), or
/// when it equals (or, inside text, contains) the current value of a sensitive field of the record in
/// question, so a value copied into a variable or a rendered message is caught too.
/// </summary>
internal sealed class Masker
{
    /// <summary>What a masked value is replaced with.</summary>
    public const string Hidden = "***";

    /// <summary>Text shorter than this is never matched by value (it would hide unrelated short values).</summary>
    private const int MinValueLength = 4;

    private readonly HashSet<string> _keys;
    private readonly List<string> _values;

    private Masker(HashSet<string> keys, List<string> values)
    {
        _keys = keys;
        _values = values;
    }

    /// <summary>A masker that hides nothing.</summary>
    public static readonly Masker None = new([], []);

    /// <summary>The sensitive keys for a registry and settings, and the sensitive values of one record (if given).</summary>
    public static Masker Create(WorkflowRegistry registry, GlobalSettings settings, IReadOnlyDictionary<string, object?>? record = null, string? entity = null)
    {
        var keys = SensitiveKeys(registry, settings);
        var values = new List<string>();
        if (record is not null && entity is not null)
        {
            foreach (var f in registry.Entity(entity)?.Fields ?? [])
            {
                if (!IsSensitive(f, entity, settings)) continue;
                CollectValues(record.GetValueOrDefault(f.Key), values);
            }
        }
        return new Masker(keys, values.Distinct().OrderByDescending(v => v.Length).ToList());
    }

    /// <summary>Every key whose value is hidden: sensitive registry fields and the <c>maskedFields</c> settings.</summary>
    public static HashSet<string> SensitiveKeys(WorkflowRegistry registry, GlobalSettings settings)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in registry.Entities)
        foreach (var f in e.Fields)
            if (f.IsSensitive) keys.Add(f.Key);
        foreach (var m in settings.Audit?.MaskedFields ?? [])
        {
            var dot = m.IndexOf('.');
            if (dot > 0 && dot < m.Length - 1) keys.Add(m[(dot + 1)..]);
        }
        return keys;
    }

    private static bool IsSensitive(FieldDescriptor f, string entity, GlobalSettings settings) =>
        f.IsSensitive || (settings.Audit?.MaskedFields ?? []).Contains($"{entity}.{f.Key}");

    private static void CollectValues(object? v, List<string> into)
    {
        switch (Values.Normalize(v))
        {
            case null: return;
            case List<object?> l:
                foreach (var x in l) CollectValues(x, into);
                return;
            case Dictionary<string, object?> d:
                foreach (var x in d.Values) CollectValues(x, into);
                return;
            case bool:
                return;
            case var other:
                var text = Values.Text(other);
                if (text.Length >= MinValueLength) into.Add(text);
                return;
        }
    }

    /// <summary>A copy of <paramref name="value"/> with sensitive parts replaced by <c>"***"</c>.</summary>
    public object? Mask(object? value) => MaskValue(Values.Normalize(value));

    /// <summary>Masks a dictionary (see <see cref="Mask"/>).</summary>
    public Dictionary<string, object?> MaskMap(IReadOnlyDictionary<string, object?> map) =>
        (Dictionary<string, object?>)MaskValue(map.ToDictionary(kv => kv.Key, kv => Values.Normalize(kv.Value), StringComparer.Ordinal))!;

    /// <summary>Masks a trigger value: also hides <c>from</c>/<c>to</c> when the trigger watched a sensitive field.</summary>
    public object? MaskTrigger(object? trigger)
    {
        var masked = Mask(trigger);
        if (masked is Dictionary<string, object?> d && d.GetValueOrDefault("field") is string f && _keys.Contains(f))
        {
            if (d.GetValueOrDefault("from") is not null) d["from"] = Hidden;
            if (d.GetValueOrDefault("to") is not null) d["to"] = Hidden;
        }
        return masked;
    }

    /// <summary>Masks text: every occurrence of a sensitive value is replaced.</summary>
    public string? MaskText(string? text)
    {
        if (text is null || _values.Count == 0) return text;
        foreach (var v in _values) text = text.Replace(v, Hidden, StringComparison.Ordinal);
        return text;
    }

    private object? MaskValue(object? v) => v switch
    {
        Dictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => _keys.Contains(kv.Key) && kv.Value is not null ? Hidden : MaskValue(kv.Value), StringComparer.Ordinal),
        List<object?> l => l.Select(MaskValue).ToList(),
        string s => _values.Contains(s) ? Hidden : MaskText(s),
        double n when _values.Count > 0 && _values.Contains(Values.NumberText(n)) => Hidden,
        _ => v,
    };
}
