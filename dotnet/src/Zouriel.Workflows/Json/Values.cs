using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zouriel.Workflows.Json;

/// <summary>
/// Workflow values are plain JSON values held as CLR objects: <c>null</c>, <see cref="string"/>,
/// <see cref="double"/>, <see cref="bool"/>, <see cref="List{T}"/> of values and
/// <see cref="Dictionary{TKey,TValue}"/> of string to value. This mirrors what the form engine works
/// with in TypeScript and Dart, so conditions behave identically everywhere.
/// </summary>
public static class Values
{
    /// <summary>Turns any CLR value (entity property, JSON node, boxed number…) into a workflow value.</summary>
    public static object? Normalize(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b,
        double d => d,
        float f => (double)f,
        decimal m => (double)m,
        int i => (double)i,
        long l => (double)l,
        short sh => (double)sh,
        byte by => (double)by,
        uint ui => (double)ui,
        ulong ul => (double)ul,
        Guid g => g.ToString(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
        DateTime dt => FormatInstant(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
        DateTimeOffset dto => FormatInstant(dto.UtcDateTime),
        Enum e => e.ToString(),
        JsonNode n => FromJson(n),
        JsonElement el => FromJson(JsonNode.Parse(el.GetRawText())),
        IDictionary<string, object?> dict => dict.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)),
        System.Collections.IDictionary dict => dict.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => Normalize(dict[k])),
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Normalize).ToList(),
        _ => v.ToString(),
    };

    /// <summary>An instant as ISO 8601 UTC with milliseconds (<c>2026-01-02T03:04:05.000Z</c>).</summary>
    public static string FormatInstant(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>An instant as ISO 8601 UTC with milliseconds.</summary>
    public static string FormatInstant(DateTimeOffset t) => FormatInstant(t.UtcDateTime);

    /// <summary>A JSON node as a workflow value: dictionaries, lists, strings, doubles, booleans or null.</summary>
    public static object? FromJson(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kv in o) d[kv.Key] = FromJson(kv.Value);
                return d;
            case JsonArray a:
                return a.Select(FromJson).ToList();
            case JsonValue v:
                var el = v.GetValue<JsonElement>();
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
        }
        return null;
    }

    /// <summary>Parses JSON text into a workflow value.</summary>
    public static object? FromJson(string json) => FromJson(JsonNode.Parse(json));

    /// <summary>A workflow (or CLR) value as a JSON node.</summary>
    public static JsonNode? ToJson(object? v)
    {
        v = Normalize(v);
        switch (v)
        {
            case null: return null;
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case double d:
                if (d == Math.Floor(d) && Math.Abs(d) < 9007199254740992d) return JsonValue.Create((long)d);
                return JsonValue.Create(d);
            case List<object?> l:
                var arr = new JsonArray();
                foreach (var x in l) arr.Add(ToJson(x));
                return arr;
            case Dictionary<string, object?> m:
                var obj = new JsonObject();
                foreach (var kv in m) obj[kv.Key] = ToJson(kv.Value);
                return obj;
        }
        return JsonValue.Create(v.ToString());
    }

    /// <summary>A workflow value as JSON text (<c>null</c> for null).</summary>
    public static string Serialize(object? v) => ToJson(v)?.ToJsonString() ?? "null";

    /// <summary>True for null, blank text and empty lists or objects (the form engine's notion of empty).</summary>
    public static bool IsEmpty(object? v) =>
        v is null || (v is string s && s.Length == 0) || (v is List<object?> l && l.Count == 0) || (v is double d && double.IsNaN(d));

    /// <summary>Structural equality of two workflow values.</summary>
    public static bool DeepEquals(object? a, object? b)
    {
        a = Normalize(a);
        b = Normalize(b);
        switch (a)
        {
            case null: return b is null;
            case List<object?> la when b is List<object?> lb:
                return la.Count == lb.Count && la.Zip(lb).All(p => DeepEquals(p.First, p.Second));
            case Dictionary<string, object?> da when b is Dictionary<string, object?> db:
                return da.Count == db.Count && da.All(kv => db.TryGetValue(kv.Key, out var o) && DeepEquals(kv.Value, o));
            case double x when b is double y: return x.Equals(y);
            default: return Equals(a, b);
        }
    }

    /// <summary>Reads a dot path inside a value (dictionaries by key, lists by index).</summary>
    public static object? ReadPath(object? root, IEnumerable<string> parts)
    {
        var cur = root;
        foreach (var p in parts)
        {
            cur = cur switch
            {
                Dictionary<string, object?> d => d.TryGetValue(p, out var x) ? x : null,
                List<object?> l when int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < l.Count => l[i],
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>Shows a value as text the way a template renders it.</summary>
    public static string Text(object? v) => Normalize(v) switch
    {
        null => "",
        string s => s,
        bool b => b ? "Yes" : "No",
        double d => NumberText(d),
        List<object?> l => string.Join(", ", l.Select(Text)),
        Dictionary<string, object?> m when m.TryGetValue("label", out var label) => Text(label),
        var o => Serialize(o),
    };

    /// <summary>Formats a number like JavaScript's <c>String(n)</c> (shortest round-trip form).</summary>
    public static string NumberText(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return "0";
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        if (!r.Contains('E')) return r;
        // .NET writes 1E+21 / 1E-07; JavaScript writes 1e+21 / 1e-7, and uses plain notation
        // from 1e-6 up to 1e21.
        var parts = r.Split('E');
        var mantissa = parts[0];
        var exp = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var abs = Math.Abs(d);
        if (abs >= 1e-6 && abs < 1e21)
        {
            var neg = mantissa.StartsWith('-');
            var digits = mantissa.TrimStart('-').Replace(".", "");
            var pointPos = mantissa.TrimStart('-').IndexOf('.') is var ix && ix >= 0 ? ix : mantissa.TrimStart('-').Length;
            pointPos += exp;
            string s;
            if (pointPos <= 0) s = "0." + new string('0', -pointPos) + digits;
            else if (pointPos >= digits.Length) s = digits + new string('0', pointPos - digits.Length);
            else s = digits[..pointPos] + "." + digits[pointPos..];
            return (neg ? "-" : "") + s;
        }
        return mantissa + "e" + (exp > 0 ? "+" : "-") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>JSON Canonicalization Scheme (RFC 8785) serialisation, used for audit hashes.</summary>
    public static string Canonical(object? v)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, Normalize(v));
        return sb.ToString();
    }

    private static void WriteCanonical(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidOperationException("JCS cannot encode NaN or Infinity.");
                sb.Append(NumberText(d));
                break;
            case string s: WriteCanonicalString(sb, s); break;
            case List<object?> l:
                sb.Append('[');
                for (var i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteCanonical(sb, l[i]);
                }
                sb.Append(']');
                break;
            case Dictionary<string, object?> m:
                sb.Append('{');
                var first = true;
                // RFC 8785 orders keys by their UTF-16 code units, which is ordinal string order in .NET.
                foreach (var kv in m.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteCanonicalString(sb, kv.Key);
                    sb.Append(':');
                    WriteCanonical(sb, kv.Value);
                }
                sb.Append('}');
                break;
            default: WriteCanonicalString(sb, v.ToString() ?? ""); break;
        }
    }

    private static void WriteCanonicalString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
