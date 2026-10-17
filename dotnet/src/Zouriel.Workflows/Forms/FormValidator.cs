using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Forms;

/// <summary>A problem with submitted form answers, keyed by the answer's path (e.g. <c>remark</c>, <c>items[1].qty</c>).</summary>
public sealed record FormError(string Path, string FieldId, string Message);

/// <summary>Checks answers against a <c>@zouriel/ui</c> form-builder definition on the server.</summary>
public interface IFormValidator
{
    IReadOnlyList<FormError> Validate(JsonObject form, IReadOnlyDictionary<string, object?> data, string today);
}

/// <summary>
/// Server-side checks for form answers: page, section and field visibility rules, required
/// (including conditional required), the built-in checks and the implicit email/url/phone formats,
/// all with the form builder's wording. Answers to hidden fields are ignored, as in the renderer.
/// App-registered (custom) checks are browser-only and are not run here.
/// </summary>
public sealed partial class FormValidator : IFormValidator
{
    public IReadOnlyList<FormError> Validate(JsonObject form, IReadOnlyDictionary<string, object?> data, string today)
    {
        var index = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var page in form["pages"] as JsonArray ?? [])
            if (page is JsonObject p) IndexElements(p["elements"] as JsonArray, index, null, [], p);
        var errors = new List<FormError>();
        var root = data.ToDictionary(kv => kv.Key, kv => Values.Normalize(kv.Value), StringComparer.Ordinal);
        foreach (var page in form["pages"] as JsonArray ?? [])
        {
            if (page is not JsonObject p) continue;
            if (!Shown(p, new Ctx(index, root, null, today))) continue;
            Walk(p["elements"] as JsonArray, index, root, null, today, errors);
        }
        return errors;
    }

    private sealed record Entry(JsonObject Element, string Key, List<string> Path, string? RepeatGroupId);

    private static void IndexElements(JsonArray? elements, Dictionary<string, Entry> index, string? repeat, List<string> path, JsonObject page)
    {
        foreach (var el in elements ?? [])
        {
            if (el is not JsonObject o) continue;
            var id = o["id"]?.GetValue<string>() ?? "";
            var kind = o["kind"]?.GetValue<string>();
            var key = o["key"]?.GetValue<string>();
            if (kind == "field")
            {
                index[id] = new Entry(o, key ?? id, [.. path, key ?? id], repeat);
            }
            else if (kind == "group")
            {
                var isRepeat = o["repeat"] is JsonObject || (o["repeat"] is JsonValue jv && jv.TryGetValue<bool>(out var b) && b);
                var groupPath = key is { Length: > 0 } ? [.. path, key] : path;
                index[id] = new Entry(o, key ?? id, groupPath, repeat);
                IndexElements(o["elements"] as JsonArray, index, isRepeat ? id : repeat, isRepeat ? [] : groupPath, page);
            }
        }
    }

    private sealed class Ctx(Dictionary<string, Entry> index, Dictionary<string, object?> root, Dictionary<string, object?>? row, string today) : IValueContext
    {
        public Dictionary<string, Entry> Index { get; } = index;
        public Dictionary<string, object?> Root { get; } = root;
        public Dictionary<string, object?>? Row { get; } = row;
        public string Today { get; } = today;

        public object? Read(string path)
        {
            if (!Index.TryGetValue(path, out var e)) return null;
            if (e.RepeatGroupId is not null)
                return Row is null ? null : Values.ReadPath(Row, e.Path);
            return Values.ReadPath(Root, e.Path);
        }
    }

    private static bool Shown(JsonObject el, Ctx ctx)
    {
        if (el["archived"] is JsonValue a && a.TryGetValue<bool>(out var archived) && archived) return false;
        if (el["visibility"] is not JsonObject vis) return true;
        var when = vis["when"]?.Deserialize<Condition>(Definitions.JsonOptions);
        var action = vis["action"]?.GetValue<string>() ?? "show";
        return Conditions.Evaluate(when, ctx) == (action == "show");
    }

    private static bool Toggle(JsonNode? toggle, Ctx ctx) => toggle switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonObject o when o["when"] is JsonObject w => Conditions.Evaluate(w.Deserialize<Condition>(Definitions.JsonOptions), ctx),
        _ => false,
    };

    private void Walk(JsonArray? elements, Dictionary<string, Entry> index, Dictionary<string, object?> root, (Dictionary<string, object?> Row, string Prefix)? row, string today, List<FormError> errors)
    {
        var ctx = new Ctx(index, root, row?.Row, today);
        foreach (var el in elements ?? [])
        {
            if (el is not JsonObject o) continue;
            var kind = o["kind"]?.GetValue<string>();
            if (kind == "static" || !Shown(o, ctx)) continue;
            var id = o["id"]?.GetValue<string>() ?? "";
            if (!index.TryGetValue(id, out var entry)) continue;
            if (kind == "group")
            {
                var repeat = o["repeat"];
                var isRepeat = repeat is JsonObject || (repeat is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb);
                if (!isRepeat)
                {
                    Walk(o["elements"] as JsonArray, index, root, row, today, errors);
                    continue;
                }
                var rows = Values.ReadPath(row?.Row ?? root, entry.Path) as List<object?> ?? [];
                var label = o["label"]?.GetValue<string>() ?? entry.Key;
                if (repeat is JsonObject ro)
                {
                    var min = ro["min"]?.GetValue<double>();
                    var max = ro["max"]?.GetValue<double>();
                    var prefix = (row?.Prefix ?? "") + string.Join('.', entry.Path);
                    if (min is not null && rows.Count < min) errors.Add(new FormError(prefix, id, $"Add at least {min} entries to {label}."));
                    if (max is not null && rows.Count > max) errors.Add(new FormError(prefix, id, $"{label} can have at most {max} entries."));
                }
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is Dictionary<string, object?> r)
                        Walk(o["elements"] as JsonArray, index, root, (r, $"{(row?.Prefix ?? "")}{string.Join('.', entry.Path)}[{i}]."), today, errors);
                }
                continue;
            }
            if (kind != "field") continue;
            var type = o["type"]?.GetValue<string>() ?? "text";
            if (type is "calculated" or "hidden") continue;
            var value = Conditions.OptionValueOf(ctx.Read(id));
            var fieldLabel = o["label"]?.GetValue<string>() ?? entry.Key;
            var path = (row?.Prefix ?? "") + string.Join('.', entry.Path);
            var empty = Values.IsEmpty(value) || (type is "checkbox" or "toggle" && value is false);
            if (Toggle(o["required"], ctx) && empty)
            {
                var msg = o["requiredMessage"]?.GetValue<string>() ?? RequiredMessage(fieldLabel);
                errors.Add(new FormError(path, id, Format(msg, new() { ["label"] = fieldLabel })));
                continue;
            }
            if (empty) continue;
            var rules = new List<(string Type, JsonObject? Params, string? Message, JsonObject? When, string Severity)>();
            var implicitType = type switch { "email" => "email", "url" => "url", "phone" => "phone", _ => null };
            var declared = o["validators"] as JsonArray ?? [];
            if (implicitType is not null && !declared.OfType<JsonObject>().Any(v => v["type"]?.GetValue<string>() == implicitType))
                rules.Add((implicitType, null, null, null, "error"));
            foreach (var v in declared.OfType<JsonObject>())
            {
                rules.Add((v["type"]?.GetValue<string>() ?? "", v["params"] as JsonObject, v["message"]?.GetValue<string>(),
                    v["when"] as JsonObject, v["severity"]?.GetValue<string>() ?? "error"));
            }
            foreach (var rule in rules)
            {
                if (rule.Severity == "warning") continue;
                if (rule.When is not null && !Conditions.Evaluate(rule.When.Deserialize<Condition>(Definitions.JsonOptions), ctx)) continue;
                var p = rule.Params is null ? new Dictionary<string, object?>() : (Dictionary<string, object?>)Values.FromJson(rule.Params)!;
                var (ok, defaultMessage) = Check(rule.Type, value, p, ctx, today);
                if (ok) continue;
                var vars = new Dictionary<string, object?>(p) { ["label"] = fieldLabel };
                if (p.TryGetValue("field", out var other) && other is string otherId && index.TryGetValue(otherId, out var oe))
                    vars["field"] = oe.Element["label"]?.GetValue<string>() ?? oe.Key;
                if (p.TryGetValue("comparison", out var cmp)) vars["comparison"] = ComparisonWords(cmp);
                errors.Add(new FormError(path, id, Format(rule.Message ?? defaultMessage, vars)));
                break;
            }
        }
    }

    private static string RequiredMessage(string label) =>
        label.TrimEnd().EndsWith('?') ? $"Answer “{label.Trim()}”" : "{label} is required.";

    private static string ComparisonWords(object? v) => Values.Text(v) switch
    {
        "eq" => "the same as", "gt" => "more than", "gte" => "at least", "lt" => "less than", "lte" => "at most", var s => s,
    };

    private static string Format(string template, Dictionary<string, object?> vars) =>
        PlaceholderRegex().Replace(template, m => vars.TryGetValue(m.Groups[1].Value, out var v) && !Values.IsEmpty(v) ? Values.Text(v) : m.Value);

    [GeneratedRegex(@"\{(\w+)\}")] private static partial Regex PlaceholderRegex();
    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$")] private static partial Regex EmailRegex();
    [GeneratedRegex(@"^(https?:\/\/)?([\w-]+\.)+[\w-]{2,}(\/\S*)?$", RegexOptions.IgnoreCase)] private static partial Regex UrlRegex();
    [GeneratedRegex(@"^\+?[\d\s().-]{6,20}$")] private static partial Regex PhoneRegex();

    private static readonly Dictionary<string, string> PatternPresets = new()
    {
        ["letters"] = @"^[\p{L} '\-]+$",
        ["alphanumeric"] = @"^[\p{L}\p{N}]+$",
        ["digits"] = @"^\d+$",
        ["noSpaces"] = @"^\S+$",
        ["uppercase"] = @"^[\p{Lu}\p{N} ]+$",
    };

    private static double? N(object? v) => v switch
    {
        double d => double.IsFinite(d) ? d : null,
        string s when s.Trim().Length > 0 && Conditions.TryJsNumber(s, out var n) => n,
        _ => null,
    };

    private static string Str(object? v) => v switch { null => "", double d => Values.NumberText(d), bool b => b ? "true" : "false", _ => Values.Text(v) };

    private static string? DateParam(object? v, string today)
    {
        if (v is not string s || s.Length == 0) return null;
        var m = Regex.Match(s.Trim(), @"^today\s*([+-]\s*\d+)?$", RegexOptions.IgnoreCase);
        if (m.Success) return Conditions.AddDays(today, m.Groups[1].Success ? int.Parse(m.Groups[1].Value.Replace(" ", ""), CultureInfo.InvariantCulture) : 0);
        return Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$") ? s : null;
    }

    private static int? Age(string dob, string today)
    {
        if (!Regex.IsMatch(dob, @"^\d{4}-\d{2}-\d{2}")) return null;
        int Y(string x) => int.Parse(x[..4], CultureInfo.InvariantCulture);
        int M(string x) => int.Parse(x[5..7], CultureInfo.InvariantCulture);
        int D(string x) => int.Parse(x[8..10], CultureInfo.InvariantCulture);
        var a = Y(today) - Y(dob);
        if (M(today) < M(dob) || (M(today) == M(dob) && D(today) < D(dob))) a--;
        return a;
    }

    private static int Words(object? v) => Str(v).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static (bool Ok, string DefaultMessage) Check(string type, object? v, Dictionary<string, object?> p, Ctx ctx, string today)
    {
        var s = Str(v);
        switch (type)
        {
            case "email": return (EmailRegex().IsMatch(s.Trim()), "Enter an email address like name@example.com.");
            case "url": return (UrlRegex().IsMatch(s.Trim()), "Enter a web address like example.com.");
            case "phone": return (PhoneRegex().IsMatch(s.Trim()) && s.Count(char.IsAsciiDigit) >= 6, "Enter a phone number, like +44 20 7946 0958.");
            case "minLength": return (s.Length >= (N(p.GetValueOrDefault("min")) ?? 0), "{label} must be at least {min} characters.");
            case "maxLength": return (s.Length <= (N(p.GetValueOrDefault("max")) ?? double.PositiveInfinity), "{label} must be {max} characters or fewer.");
            case "minWords": return (Words(v) >= (N(p.GetValueOrDefault("min")) ?? 0), "{label} must be at least {min} words.");
            case "maxWords": return (Words(v) <= (N(p.GetValueOrDefault("max")) ?? double.PositiveInfinity), "{label} must be {max} words or fewer.");
            case "pattern":
            {
                var preset = Str(p.GetValueOrDefault("preset"));
                var source = PatternPresets.TryGetValue(preset, out var r) ? r : Str(p.GetValueOrDefault("regex"));
                if (source.Length == 0) return (true, "");
                try
                {
                    return (Regex.IsMatch(s, source, RegexOptions.None, TimeSpan.FromMilliseconds(200)), "{label} contains characters that are not allowed.");
                }
                catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
                {
                    return (true, "");
                }
            }
            case "min": return ((N(v) ?? 0) >= (N(p.GetValueOrDefault("min")) ?? double.NegativeInfinity), "{label} must be {min} or more.");
            case "max": return ((N(v) ?? 0) <= (N(p.GetValueOrDefault("max")) ?? double.PositiveInfinity), "{label} must be {max} or less.");
            case "integer": return (N(v) is { } i && Math.Floor(i) == i, "{label} must be a whole number.");
            case "decimalPlaces":
            {
                var t = N(v) is { } dv ? Values.NumberText(dv) : "";
                var dot = t.IndexOf('.');
                return (dot < 0 || t.Length - dot - 1 <= (N(p.GetValueOrDefault("places")) ?? 0), "{label} can have at most {places} decimal places.");
            }
            case "minDate": { var d = DateParam(p.GetValueOrDefault("date"), today); return (d is null || string.CompareOrdinal(s, d) >= 0, "{label} must be on or after {date}."); }
            case "maxDate": { var d = DateParam(p.GetValueOrDefault("date"), today); return (d is null || string.CompareOrdinal(s, d) <= 0, "{label} must be on or before {date}."); }
            case "ageAtLeast": return ((Age(s, today) ?? int.MaxValue) >= (N(p.GetValueOrDefault("years")) ?? 0), "You must be at least {years} years old.");
            case "ageAtMost": return ((Age(s, today) ?? int.MinValue) <= (N(p.GetValueOrDefault("years")) ?? double.PositiveInfinity), "You must be {years} years old or younger.");
            case "earliestTime": return (Values.IsEmpty(p.GetValueOrDefault("time")) || string.CompareOrdinal(s, Str(p["time"])) >= 0, "{label} must be {time} or later.");
            case "latestTime": return (Values.IsEmpty(p.GetValueOrDefault("time")) || string.CompareOrdinal(s, Str(p["time"])) <= 0, "{label} must be {time} or earlier.");
            case "minSelected": return ((v is List<object?> l1 ? l1.Count : 0) >= (N(p.GetValueOrDefault("count")) ?? 0), "Choose at least {count} options for {label}.");
            case "maxSelected": return ((v is List<object?> l2 ? l2.Count : 0) <= (N(p.GetValueOrDefault("count")) ?? double.PositiveInfinity), "Choose no more than {count} options for {label}.");
            case "maxFiles": return ((v is List<object?> l3 ? l3.Count : 0) <= (N(p.GetValueOrDefault("count")) ?? double.PositiveInfinity), "Upload no more than {count} files.");
            case "equalsField":
            {
                var other = ctx.Read(Str(p.GetValueOrDefault("field")));
                return (Values.IsEmpty(other) || s.Trim().ToLowerInvariant() == Str(other).Trim().ToLowerInvariant(), "{label} must match {field}.");
            }
            case "compareField":
            {
                var other = ctx.Read(Str(p.GetValueOrDefault("field")));
                if (Values.IsEmpty(other)) return (true, "");
                int c;
                if (N(v) is { } a && N(other) is { } b) c = a.CompareTo(b);
                else c = string.CompareOrdinal(s, Str(other));
                var ok = Str(p.GetValueOrDefault("comparison")) switch
                {
                    "eq" => c == 0, "gt" => c > 0, "gte" => c >= 0, "lt" => c < 0, "lte" => c <= 0, _ => true,
                };
                return (ok, "{label} must be {comparison} {field}.");
            }
        }
        // fileType, maxFileSize and app-registered checks need the browser's file data or app code.
        return (true, "");
    }
}
