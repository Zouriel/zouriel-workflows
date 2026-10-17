using System.Globalization;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Expressions;

/// <summary>
/// The form builder's calculations (<c>calc.ts</c>), with token paths as references. Rounding
/// follows JavaScript's <c>Math.round</c> (halves go up, also for negatives).
/// </summary>
public static class Calculations
{
    /// <summary>What a calculation reads. In workflows every reference has one value; forms have one per repeat entry.</summary>
    public interface ISource
    {
        IReadOnlyList<object?> Values(string path);
        /// <summary>Number of entries when the reference is a repeatable group, else null.</summary>
        int? Rows(string path);
        string Today { get; }
    }

    private sealed class SingleValues(IValueContext ctx) : ISource
    {
        public IReadOnlyList<object?> Values(string path) => [ctx.Read(path)];
        public int? Rows(string path) => null;
        public string Today => ctx.Today;
    }

    public static object? Evaluate(Calc? c, IValueContext ctx) => Evaluate(c, new SingleValues(ctx));

    public static object? Evaluate(Calc? c, ISource ctx)
    {
        if (c is null) return null;
        var perArg = c.Args.Select(a => ArgValues(a, ctx)).ToList();
        var nums = perArg.SelectMany(x => x).Select(ToNum).Where(n => n is not null).Select(n => n!.Value).ToList();
        switch (c.Op)
        {
            case "sum": return nums.Count > 0 ? Round(c, nums.Sum()) : null;
            case "subtract":
            {
                if (nums.Count == 0) return null;
                var totals = perArg.Select(vs => vs.Select(ToNum).Where(n => n is not null).Sum(n => n!.Value)).ToList();
                return Round(c, (totals.FirstOrDefault()) - totals.Skip(1).Sum());
            }
            case "multiply": return nums.Count > 0 ? Round(c, nums.Aggregate(1d, (s, n) => s * n)) : null;
            case "divide":
            {
                var a = ToNum(perArg.ElementAtOrDefault(0)?.FirstOrDefault());
                var b = ToNum(perArg.ElementAtOrDefault(1)?.FirstOrDefault());
                return a is null || b is null || b == 0 ? null : Round(c, a.Value / b.Value);
            }
            case "average": return nums.Count > 0 ? Round(c, nums.Sum() / nums.Count) : null;
            case "min": return nums.Count > 0 ? Round(c, nums.Min()) : null;
            case "max": return nums.Count > 0 ? Round(c, nums.Max()) : null;
            case "count":
            {
                double n = 0;
                for (var i = 0; i < c.Args.Count; i++)
                {
                    if (c.Args[i].Ref is { } r && ctx.Rows(r) is { } rows)
                    {
                        n += rows;
                        continue;
                    }
                    foreach (var v in perArg[i]) n += v is List<object?> l ? l.Count : Json.Values.IsEmpty(v) || v is false ? 0 : 1;
                }
                return n;
            }
            case "ageYears":
            case "daysBetween":
            {
                var from = ParseDate(perArg.ElementAtOrDefault(0)?.FirstOrDefault());
                var second = perArg.ElementAtOrDefault(1)?.FirstOrDefault();
                var to = ParseDate(second ?? ctx.Today);
                if (from is null || to is null) return null;
                if (c.Op == "daysBetween") return (double)(to.Value.DayNumber - from.Value.DayNumber);
                var years = to.Value.Year - from.Value.Year;
                if (to.Value.Month < from.Value.Month || (to.Value.Month == from.Value.Month && to.Value.Day < from.Value.Day)) years--;
                return (double)years;
            }
            case "concat":
            {
                var text = perArg.SelectMany(x => x).Where(v => !Json.Values.IsEmpty(v))
                    .Select(v => v is List<object?> l ? string.Join(", ", l.Select(Json.Values.Text)) : JsString(v)).ToList();
                return text.Count > 0 ? string.Join(" ", text) : null;
            }
        }
        return null;
    }

    private static string JsString(object? v) => v switch
    {
        double d => Json.Values.NumberText(d),
        bool b => b ? "true" : "false",
        _ => Json.Values.Text(v),
    };

    private static List<object?> ArgValues(CalcArg a, ISource ctx)
    {
        if (a.Ref is not null) return [.. ctx.Values(a.Ref)];
        if (a.Calc is not null) return [Evaluate(a.Calc, ctx)];
        return [a.Const];
    }

    private static double? ToNum(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        double d => double.IsFinite(d) ? d : null,
        string s when s.Trim().Length > 0 && Conditions.TryJsNumber(s, out var n) => n,
        _ => null,
    };

    private static DateOnly? ParseDate(object? v)
    {
        if (v is not string s || s.Length < 10) return null;
        return DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static object? Round(Calc c, double n)
    {
        if (!double.IsFinite(n)) return null;
        if (c.Decimals is null) return JsRound(n * 1e10) / 1e10;
        var f = Math.Pow(10, c.Decimals.Value);
        return JsRound(n * f) / f;
    }

    /// <summary>JavaScript's <c>Math.round</c>.</summary>
    public static double JsRound(double x) => Math.Floor(x + 0.5);
}
