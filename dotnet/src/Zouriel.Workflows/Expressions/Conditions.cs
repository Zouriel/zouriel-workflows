using System.Globalization;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Expressions;

/// <summary>What conditions, operands and templates can read.</summary>
public interface IValueContext
{
    /// <summary>The value at a token path (spec §3), or <c>null</c>.</summary>
    object? Read(string path);

    /// <summary>Today as <c>YYYY-MM-DD</c> in the settings time zone.</summary>
    string Today { get; }
}

/// <summary>
/// The form builder's condition operators, ported exactly from
/// <c>@zouriel/ui/form-builder</c> <c>conditions.ts</c> (the reference implementation). The shared
/// fixture <c>fixtures/conditions/conditions.json</c> proves they agree.
/// </summary>
public static class Conditions
{
    private static readonly HashSet<string> NoValue = ["empty", "notEmpty", "isTrue", "isFalse", "visible", "hidden", "complete", "incomplete"];
    private static readonly HashSet<string> TwoValues = ["between"];

    /// <summary>How many values an operator needs (0, 1 or 2).</summary>
    public static int Arity(string op) => NoValue.Contains(op) ? 0 : TwoValues.Contains(op) ? 2 : 1;

    /// <summary>Whether a condition holds (spec §4): an empty group or no condition holds; unfinished rules never match.</summary>
    public static bool Evaluate(Condition? c, IValueContext ctx)
    {
        switch (c)
        {
            case null:
                return true;
            case ConditionGroup g:
                if (g.Conditions.Count == 0) return true;
                return g.Match switch
                {
                    "all" => g.Conditions.All(x => Evaluate(x, ctx)),
                    "any" => g.Conditions.Any(x => Evaluate(x, ctx)),
                    _ => !g.Conditions.Any(x => Evaluate(x, ctx)),
                };
            case ConditionRule r:
                return EvalRule(r, ctx);
        }
        return false;
    }

    /// <summary>A saved choice <c>{value, label}</c> compares by its value.</summary>
    public static object? OptionValueOf(object? v) => v switch
    {
        List<object?> l => l.Select(OptionValueOf).ToList(),
        Dictionary<string, object?> d when d.ContainsKey("value") => d["value"],
        _ => v,
    };

    /// <summary>The value of an operand: a constant, a token, today plus days, or a calculation.</summary>
    public static object? ResolveOperand(Operand? o, IValueContext ctx)
    {
        if (o is null) return null;
        if (o.HasConst) return o.Const;
        if (o.Ref is not null) return ctx.Read(o.Ref);
        if (o.Today is not null) return AddDays(ctx.Today, o.Today.Value);
        if (o.Calc is not null) return Calculations.Evaluate(o.Calc, ctx);
        return null;
    }

    /// <summary>A <c>YYYY-MM-DD</c> date moved by a number of days.</summary>
    public static string AddDays(string iso, int days)
    {
        var d = DateOnly.ParseExact(iso[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return d.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    internal static double? Num(object? v)
    {
        if (v is double d) return double.IsFinite(d) ? d : null;
        if (v is string s && s.Trim().Length > 0 && TryJsNumber(s, out var n)) return n;
        return null;
    }

    /// <summary>JavaScript's <c>Number(s)</c> for the cases that matter: decimal, signed, exponent, whitespace.</summary>
    internal static bool TryJsNumber(string s, out double n)
    {
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var ok = long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex);
            n = hex;
            return ok;
        }
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out n) && double.IsFinite(n);
    }

    /// <summary>Orders numbers numerically and everything else (ISO dates, HH:MM times) as text.</summary>
    private static int? Compare(object? a, object? b)
    {
        if (Values.IsEmpty(a) || Values.IsEmpty(b)) return null;
        var na = Num(a);
        var nb = Num(b);
        if (na is not null && nb is not null) return Math.Sign(na.Value - nb.Value);
        var sa = JsString(a);
        var sb = JsString(b);
        return Math.Sign(string.CompareOrdinal(sa, sb));
    }

    /// <summary>JavaScript's <c>String(v)</c>.</summary>
    private static string JsString(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        double d => Values.NumberText(d),
        string s => s,
        List<object?> l => string.Join(",", l.Select(JsString)),
        _ => "[object Object]",
    };

    /// <summary>Whether <c>eq</c> treats two values as equal (case, surrounding spaces, numbers and yes/no are compared loosely).</summary>
    public static bool Same(object? a, object? b)
    {
        if (a is bool || b is bool) return Truthy(a) == Truthy(b);
        var c = Compare(a, b);
        if (c is not null && Num(a) is not null && Num(b) is not null) return c == 0;
        return NormJs(a) == NormJs(b);
    }

    private static string NormJs(object? v) => (v is null ? "" : JsString(v)).Trim().ToLowerInvariant();

    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        _ => true,
    };

    private static List<object?> AsList(object? v) => v is List<object?> l ? l : Values.IsEmpty(v) ? [] : [v];

    private static bool EvalRule(ConditionRule r, IValueContext ctx)
    {
        var v = OptionValueOf(ctx.Read(r.Ref));
        switch (r.Op)
        {
            case "empty": return Values.IsEmpty(v) || v is false;
            case "notEmpty": return !Values.IsEmpty(v) && v is not false;
            case "isTrue": return v is true;
            case "isFalse": return v is not true;
        }
        var target = OptionValueOf(ResolveOperand(r.Value, ctx));
        // An unfinished rule ("Age is more than …") never matches.
        if (Arity(r.Op) > 0 && Values.IsEmpty(target))
        {
            return r.Op is "neq" or "noneOf" or "excludes" && !Values.IsEmpty(v);
        }
        switch (r.Op)
        {
            case "eq": return Same(v, target);
            case "neq": return !Same(v, target);
            case "contains": return NormJs(v).Contains(NormJs(target), StringComparison.Ordinal);
            case "notContains": return !NormJs(v).Contains(NormJs(target), StringComparison.Ordinal);
            case "startsWith": return NormJs(v).StartsWith(NormJs(target), StringComparison.Ordinal);
            case "endsWith": return NormJs(v).EndsWith(NormJs(target), StringComparison.Ordinal);
            case "gt" or "after": { var c = Compare(v, target); return c is > 0; }
            case "gte" or "onOrAfter": { var c = Compare(v, target); return c is >= 0; }
            case "lt" or "before": { var c = Compare(v, target); return c is < 0; }
            case "lte" or "onOrBefore": { var c = Compare(v, target); return c is <= 0; }
            case "between":
            {
                var hi = OptionValueOf(ResolveOperand(r.Value2, ctx));
                var lo = Compare(v, target);
                var up = Compare(v, hi);
                return lo is >= 0 && up is <= 0;
            }
            case "anyOf": return AsList(target).Any(t => Same(v, t));
            case "noneOf": return !AsList(target).Any(t => Same(v, t));
            case "includes": return AsList(v).Any(x => Same(x, target));
            case "excludes": return !AsList(v).Any(x => Same(x, target));
            case "includesAny": return AsList(target).Any(t => AsList(v).Any(x => Same(x, t)));
            case "includesAll": return AsList(target).All(t => AsList(v).Any(x => Same(x, t)));
            case "countGte": return AsList(v).Count >= (Num(target) ?? double.PositiveInfinity);
            case "countLte": return AsList(v).Count <= (Num(target) ?? double.NegativeInfinity);
        }
        return false;
    }

    /// <summary>Whether every rule has what it needs (a path, and values where needed).</summary>
    public static bool IsComplete(Condition? c)
    {
        switch (c)
        {
            case null: return true;
            case ConditionGroup g: return g.Conditions.All(IsComplete);
            case ConditionRule r:
                if (string.IsNullOrEmpty(r.Ref)) return false;
                static bool Filled(Operand? o) => o is not null && (o.Ref is { Length: > 0 } || o.Today is not null || o.Calc is not null || (o.HasConst && !Values.IsEmpty(o.Const)));
                var arity = Arity(r.Op);
                if (arity >= 1 && !Filled(r.Value)) return false;
                if (arity == 2 && !Filled(r.Value2)) return false;
                return true;
        }
        return false;
    }

    /// <summary>Every token path a condition reads.</summary>
    public static IEnumerable<string> Refs(Condition? c)
    {
        switch (c)
        {
            case ConditionGroup g:
                foreach (var x in g.Conditions)
                foreach (var r in Refs(x)) yield return r;
                break;
            case ConditionRule rule:
                if (!string.IsNullOrEmpty(rule.Ref)) yield return rule.Ref;
                foreach (var o in new[] { rule.Value, rule.Value2 })
                {
                    if (o?.Ref is { Length: > 0 } p) yield return p;
                    if (o?.Calc is { } calc)
                        foreach (var r in CalcRefs(calc)) yield return r;
                }
                break;
        }
    }

    /// <summary>Every token path a calculation reads, nested calculations included.</summary>
    public static IEnumerable<string> CalcRefs(Calc c) =>
        c.Args.SelectMany(a => a.Ref is not null ? [a.Ref] : a.Calc is not null ? CalcRefs(a.Calc) : []);
}
