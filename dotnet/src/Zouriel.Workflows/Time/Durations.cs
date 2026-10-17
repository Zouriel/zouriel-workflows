using System.Globalization;
using System.Text.RegularExpressions;

namespace Zouriel.Workflows.Time;

/// <summary>An ISO 8601 duration (spec §9), kept in its parts so months and days stay calendar-aware.</summary>
public readonly partial record struct IsoDuration(bool Negative, int Years, int Months, int Weeks, int Days, int Hours, int Minutes, int Seconds)
{
    [GeneratedRegex(@"^(-)?P(?!$)(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?=\d)(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$")]
    private static partial Regex Pattern();

    /// <summary>The longest duration accepted (50 years); longer ones are refused as invalid.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(50 * 365);

    /// <summary>Reads an ISO 8601 duration. False for malformed text and for durations longer than <see cref="MaxLength"/>.</summary>
    public static bool TryParse(string? s, out IsoDuration d)
    {
        d = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = Pattern().Match(s.Trim());
        if (!m.Success) return false;
        var parts = new int[9];
        for (var i = 2; i <= 8; i++)
        {
            if (!m.Groups[i].Success) continue;
            // Anything that does not fit an int is far beyond MaxLength anyway.
            if (!int.TryParse(m.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]) || parts[i] > 50 * 366 * 24 * 60) return false;
        }
        var days = parts[2] * 365.0 + parts[3] * 30.0 + parts[4] * 7.0 + parts[5] + parts[6] / 24.0 + parts[7] / 1440.0 + parts[8] / 86400.0;
        if (days > MaxLength.TotalDays) return false;
        var candidate = new IsoDuration(m.Groups[1].Success, parts[2], parts[3], parts[4], parts[5], parts[6], parts[7], parts[8]);
        d = candidate;
        return true;
    }

    /// <summary>Reads an ISO 8601 duration, refusing invalid ones with the code <c>badDuration</c>.</summary>
    public static IsoDuration Parse(string s) =>
        TryParse(s, out var d) ? d : throw new WorkflowException("badDuration", $"'{s}' is not a duration (ISO 8601, at most 50 years).");

    /// <summary>True when every part is zero.</summary>
    public bool IsZero => Years == 0 && Months == 0 && Weeks == 0 && Days == 0 && Hours == 0 && Minutes == 0 && Seconds == 0;

    /// <summary>Whole days in the date part (weeks count 7).</summary>
    public int TotalDays => Weeks * 7 + Days;

    /// <summary>The time part (hours, minutes, seconds).</summary>
    public TimeSpan TimePart => new(Hours, Minutes, Seconds);

    /// <summary>An approximate length, for ordering and limits only (months = 30 days).</summary>
    public TimeSpan Approximate => TimeSpan.FromDays(Years * 365.0 + Months * 30.0 + Weeks * 7.0 + Days) + TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes) + TimeSpan.FromSeconds(Seconds);
}

/// <summary>Adds durations to instants, in plain or working time.</summary>
public sealed class WorkCalendar
{
    private readonly HashSet<DayOfWeek> _days;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;
    private readonly HashSet<DateOnly> _holidays;

    /// <summary>The time zone local dates and times are in.</summary>
    public TimeZoneInfo Zone { get; }
    /// <summary>False when no working calendar is configured; working durations then count plain time.</summary>
    public bool HasWorkingTime { get; }

    /// <summary>A calendar: working <paramref name="days"/> (default Monday–Friday), hours <paramref name="start"/>–<paramref name="end"/> (default 09:00–17:00) and <paramref name="holidays"/>.</summary>
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

    /// <summary>Plain UTC time with no working calendar.</summary>
    public static WorkCalendar Utc { get; } = new(TimeZoneInfo.Utc, hasWorkingTime: false);

    /// <summary>An instant as local time in <see cref="Zone"/>.</summary>
    public DateTime ToLocal(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Zone).DateTime;

    /// <summary>A local time in <see cref="Zone"/> as an instant (times inside a DST gap move forward).</summary>
    public DateTimeOffset FromLocal(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A local time inside a DST gap does not exist; move forward to the first valid minute.
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    /// <summary>The local date (<c>YYYY-MM-DD</c>) of an instant.</summary>
    public string Today(DateTimeOffset now) => DateOnly.FromDateTime(ToLocal(now)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds a duration. <paramref name="working"/> counts only working time (spec §9). A result beyond the calendar's
    /// range (year 9999) is <see cref="DateTimeOffset.MaxValue"/> (never), before year 1 <see cref="DateTimeOffset.MinValue"/>;
    /// it never throws.
    /// </summary>
    public DateTimeOffset Add(DateTimeOffset from, IsoDuration d, bool working = false)
    {
        try
        {
            return AddUnchecked(from, d, working);
        }
        catch (ArgumentOutOfRangeException)
        {
            return d.Negative ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue;
        }
    }

    private DateTimeOffset AddUnchecked(DateTimeOffset from, IsoDuration d, bool working)
    {
        if (working && HasWorkingTime && !d.Negative) return AddWorking(from, d);
        var sign = d.Negative ? -1 : 1;
        var local = ToLocal(from);
        local = local.AddYears(sign * d.Years).AddMonths(sign * d.Months).AddDays(sign * d.TotalDays);
        var result = FromLocal(local);
        return result + (sign * d.TimePart);
    }

    /// <summary>Adds an ISO duration given as text (see the other overload).</summary>
    public DateTimeOffset Add(DateTimeOffset from, string iso, bool working = false) => Add(from, IsoDuration.Parse(iso), working);

    /// <summary>Whether a date is a working day (a working weekday and not a holiday).</summary>
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
