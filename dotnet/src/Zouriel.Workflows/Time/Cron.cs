using System.Globalization;

namespace Zouriel.Workflows.Time;

/// <summary>
/// A standard 5-field cron expression (minute hour day-of-month month day-of-week) with <c>*</c>,
/// lists, ranges and steps. Day-of-week 0 and 7 are Sunday. When both day fields are restricted a
/// day matches if either does (classic cron).
/// </summary>
public sealed class Cron
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _dom = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _dow = new bool[7];
    private readonly bool _domAny;
    private readonly bool _dowAny;

    private Cron(string[] f)
    {
        Fill(_minutes, f[0], 0, 59);
        Fill(_hours, f[1], 0, 23);
        _domAny = f[2] == "*" || f[2] == "?";
        Fill(_dom, f[2], 1, 31);
        Fill(_months, f[3], 1, 12);
        _dowAny = f[4] == "*" || f[4] == "?";
        var dow = new bool[8];
        Fill(dow, f[4], 0, 7);
        for (var i = 0; i < 7; i++) _dow[i] = dow[i];
        if (dow[7]) _dow[0] = true;
    }

    /// <summary>Reads a 5-field cron expression; false with an <paramref name="error"/> message when it is invalid.</summary>
    public static bool TryParse(string? expr, out Cron? cron, out string? error)
    {
        cron = null;
        error = null;
        var f = (expr ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
        {
            error = "A schedule needs 5 parts: minute hour day month weekday.";
            return false;
        }
        try
        {
            cron = new Cron(f);
            return true;
        }
        catch (FormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Reads a 5-field cron expression, throwing <c>FormatException</c> when it is invalid.</summary>
    public static Cron Parse(string expr) =>
        TryParse(expr, out var c, out var e) ? c! : throw new FormatException(e);

    private static void Fill(bool[] target, string field, int min, int max)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                step = ParseInt(part[(slash + 1)..], 1, max);
                range = part[..slash];
            }
            int lo, hi;
            if (range is "*" or "?")
            {
                lo = min;
                hi = max;
            }
            else if (range.Contains('-'))
            {
                var bits = range.Split('-');
                lo = ParseInt(bits[0], min, max);
                hi = ParseInt(bits[1], min, max);
                if (hi < lo) throw new FormatException($"'{part}' is a backwards range.");
            }
            else
            {
                lo = ParseInt(range, min, max);
                hi = slash >= 0 ? max : lo;
            }
            for (var i = lo; i <= hi; i += step) target[i] = true;
        }
    }

    private static int ParseInt(string s, int min, int max)
    {
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
        {
            throw new FormatException($"'{s}' must be a number from {min} to {max}.");
        }
        return v;
    }

    private bool DayMatches(DateTime local)
    {
        var dom = _dom[local.Day];
        var dow = _dow[(int)local.DayOfWeek];
        if (_domAny && _dowAny) return true;
        if (_domAny) return dow;
        if (_dowAny) return dom;
        return dom || dow;
    }

    /// <summary>The first matching minute strictly after <paramref name="after"/>, in <paramref name="zone"/>.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0).AddMinutes(1);
        var limit = local.AddYears(5);
        while (local < limit)
        {
            if (!_months[local.Month])
            {
                local = new DateTime(local.Year, local.Month, 1).AddMonths(1);
                continue;
            }
            if (!DayMatches(local))
            {
                local = local.Date.AddDays(1);
                continue;
            }
            if (!_hours[local.Hour])
            {
                local = local.Date.AddHours(local.Hour + 1);
                continue;
            }
            if (!_minutes[local.Minute])
            {
                local = local.AddMinutes(1);
                continue;
            }
            if (zone.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
                continue;
            }
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        return null;
    }
}
