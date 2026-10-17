using System.Globalization;
using System.Text.RegularExpressions;

namespace Zouriel.Workflows.Time;

/// <summary>An ISO 8601 duration (spec §9), kept in its parts so months and days stay calendar-aware.</summary>
public readonly partial record struct IsoDuration(bool Negative, int Years, int Months, int Weeks, int Days, int Hours, int Minutes, int Seconds)
{
    [GeneratedRegex(@"^(-)?P(?!$)(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?=\d)(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? s, out IsoDuration d)
    {
        d = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = Pattern().Match(s.Trim());
        if (!m.Success) return false;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        d = new IsoDuration(m.Groups[1].Success, G(2), G(3), G(4), G(5), G(6), G(7), G(8));
        return true;
    }

    public static IsoDuration Parse(string s) =>
        TryParse(s, out var d) ? d : throw new FormatException($"'{s}' is not an ISO 8601 duration.");

    public bool IsZero => Years == 0 && Months == 0 && Weeks == 0 && Days == 0 && Hours == 0 && Minutes == 0 && Seconds == 0;

    /// <summary>Whole days in the date part (weeks count 7).</summary>
    public int TotalDays => Weeks * 7 + Days;

    public TimeSpan TimePart => new(Hours, Minutes, Seconds);

    /// <summary>An approximate length, for ordering and limits only (months = 30 days).</summary>
    public TimeSpan Approximate => TimeSpan.FromDays(Years * 365 + Months * 30 + TotalDays) + TimePart;
}

/// <summary>Adds durations to instants, in plain or working time.</summary>
public sealed class WorkCalendar
{
    private readonly HashSet<DayOfWeek> _days;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;
    private readonly HashSet<DateOnly> _holidays;

    public TimeZoneInfo Zone { get; }
    public bool HasWorkingTime { get; }

    public WorkCalendar(TimeZoneInfo zone, IEnumerable<DayOfWeek>? days = null, TimeOnly? start = null, TimeOnly? end = null, IEnumerable<DateOnly>? holidays = null, bool hasWorkingTime = true)
    {
        Zone = zone;
        _days = (days ?? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]).ToHashSet();
        if (_days.Count == 0) _days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        _start = start ?? new TimeOnly(9, 0);
        _end = end ?? new TimeOnly(17, 0);
        if (_end <= _start) _end = _start.AddHours(8);
        _holidays = (holidays ?? []).ToHashSet();
        HasWorkingTime = hasWorkingTime;
    }

    public static WorkCalendar Utc { get; } = new(TimeZoneInfo.Utc, hasWorkingTime: false);

    public DateTime ToLocal(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Zone).DateTime;

    public DateTimeOffset FromLocal(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A local time inside a DST gap does not exist; move forward to the first valid minute.
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    public string Today(DateTimeOffset now) => DateOnly.FromDateTime(ToLocal(now)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Adds a duration. <paramref name="working"/> counts only working time (spec §9).</summary>
    public DateTimeOffset Add(DateTimeOffset from, IsoDuration d, bool working = false)
    {
        if (working && HasWorkingTime && !d.Negative) return AddWorking(from, d);
        var sign = d.Negative ? -1 : 1;
        var local = ToLocal(from);
        local = local.AddYears(sign * d.Years).AddMonths(sign * d.Months).AddDays(sign * d.TotalDays);
        var result = FromLocal(local);
        return result + (sign * d.TimePart);
    }

    public DateTimeOffset Add(DateTimeOffset from, string iso, bool working = false) => Add(from, IsoDuration.Parse(iso), working);

    public bool IsWorkingDay(DateOnly day) => _days.Contains(day.DayOfWeek) && !_holidays.Contains(day);

    private DateTimeOffset AddWorking(DateTimeOffset from, IsoDuration d)
    {
        var local = ToLocal(from);
        // Calendar months/years are still calendar; then whole working days; then working hours.
        local = local.AddYears(d.Years).AddMonths(d.Months);
        local = MoveIntoWorkingTime(local);
        for (var i = 0; i < d.TotalDays; i++)
        {
            local = local.AddDays(1);
            while (!IsWorkingDay(DateOnly.FromDateTime(local))) local = local.AddDays(1);
        }
        var remaining = d.TimePart;
        while (remaining > TimeSpan.Zero)
        {
            local = MoveIntoWorkingTime(local);
            var endOfDay = local.Date + _end.ToTimeSpan();
            var available = endOfDay - local;
            if (remaining <= available)
            {
                local += remaining;
                remaining = TimeSpan.Zero;
            }
            else
            {
                remaining -= available;
                local = NextWorkingStart(local.Date.AddDays(1));
            }
        }
        return FromLocal(local);
    }

    /// <summary>The same moment if it is inside working hours, else the next start of working hours.</summary>
    public DateTime MoveIntoWorkingTime(DateTime local)
    {
        var day = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        if (IsWorkingDay(day) && time >= _start && time < _end) return local;
        if (IsWorkingDay(day) && time < _start) return local.Date + _start.ToTimeSpan();
        return NextWorkingStart(local.Date.AddDays(1));
    }

    private DateTime NextWorkingStart(DateTime date)
    {
        var d = date.Date;
        for (var i = 0; i < 800 && !IsWorkingDay(DateOnly.FromDateTime(d)); i++) d = d.AddDays(1);
        return d + _start.ToTimeSpan();
    }

    /// <summary>A date (YYYY-MM-DD) at a local time of day, as an instant.</summary>
    public DateTimeOffset AtLocal(DateOnly date, TimeOnly time) => FromLocal(date.ToDateTime(time));
}
