using System.Globalization;
using System.Text.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Settings;

/// <summary>App-wide workflow settings (spec §12), edited in <c>ui-workflow-global-settings</c>.</summary>
public sealed class GlobalSettings
{
    /// <summary>The settings format version (1).</summary>
    public int Version { get; set; } = 1;
    /// <summary>IANA or Windows time zone id for calendars, date triggers and schedules; null = UTC.</summary>
    public string? TimeZone { get; set; }
    /// <summary>Working days, hours and holidays for <c>calendar: "working"</c> durations (spec §9); null = no working calendar.</summary>
    public CalendarSettings? Calendar { get; set; }
    /// <summary>Defaults steps and triggers use when they do not set their own.</summary>
    public DefaultSettings? Defaults { get; set; }
    /// <summary>Delegation default.</summary>
    public PeopleSettings? People { get; set; }
    /// <summary>Per notification channel (by registry key): enabled, footer.</summary>
    public Dictionary<string, ChannelSettings>? Channels { get; set; }
    /// <summary>Allowed webhook origins and named connections.</summary>
    public WebhookSettings? Webhooks { get; set; }
    /// <summary>Audit retention and extra masked fields.</summary>
    public AuditSettings? Audit { get; set; }
    /// <summary>Entities whose triggers are paused (nothing new starts for their records).</summary>
    public List<PausedEntity>? Paused { get; set; }

    /// <summary>Reads settings JSON; null or blank gives the defaults.</summary>
    public static GlobalSettings Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new GlobalSettings() : JsonSerializer.Deserialize<GlobalSettings>(json, Definitions.JsonOptions) ?? new GlobalSettings();

    /// <summary>The settings as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Definitions.JsonOptions);

    /// <summary>The loop protection limit (spec §5), default 5.</summary>
    public int MaxChainDepth => Defaults?.MaxChainDepth ?? 5;
    /// <summary>The most items a <c>forEach</c> covers, default 500.</summary>
    public int ForEachMax => Defaults?.ForEachMax ?? 500;
    /// <summary>The concurrency mode for automations that set none, default <c>perRecordSerial</c>.</summary>
    public string DefaultConcurrency => Defaults?.Concurrency ?? "perRecordSerial";

    /// <summary>Whether triggers of an entity are paused.</summary>
    public bool IsPaused(string? entity) => entity is not null && Paused?.Any(p => p.Entity == entity) == true;

    /// <summary>The configured time zone, or UTC when unset or unknown.</summary>
    public TimeZoneInfo Zone()
    {
        if (string.IsNullOrWhiteSpace(TimeZone)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// The calendar durations are added with (working time only when a calendar is set). Never throws: values saved
    /// before they were validated (hours that are not <c>HH:mm</c>, holidays that are not real dates, unknown day names)
    /// are left out and the defaults used, each reported to <paramref name="warn"/>, so one bad value cannot stop every
    /// unit of work. <see cref="Problems"/> refuses such values when settings are saved.
    /// </summary>
    public WorkCalendar WorkCalendar(Action<string>? warn = null)
    {
        var zone = Zone();
        if (Calendar is null) return new WorkCalendar(zone, hasWorkingTime: false);
        List<DayOfWeek>? days = null;
        if (Calendar.Days is not null)
        {
            days = [];
            foreach (var d in Calendar.Days)
            {
                if (TryParseDay(d, out var day)) days.Add(day);
                else warn?.Invoke($"Ignoring the working day '{d}' in settings: use mon, tue, wed, thu, fri, sat or sun.");
            }
        }
        TimeOnly? start = null, end = null;
        if (Calendar.Hours is { Count: 2 } && TryParseTime(Calendar.Hours[0], out var s) && TryParseTime(Calendar.Hours[1], out var e) && e > s)
        {
            start = s;
            end = e;
        }
        else if (Calendar.Hours is not null) warn?.Invoke($"Ignoring the working hours [{string.Join(", ", Calendar.Hours)}] in settings: they must be two HH:mm times, end after start. Using 09:00-17:00.");
        var holidays = new List<DateOnly>();
        foreach (var h in Calendar.Holidays ?? [])
        {
            if (TryParseDate(h, out var date)) holidays.Add(date);
            else warn?.Invoke($"Ignoring the holiday '{h}' in settings: it is not a date (YYYY-MM-DD).");
        }
        return new WorkCalendar(zone, days, start, end, holidays);
    }

    private static bool TryParseDay(string? d, out DayOfWeek day)
    {
        day = d switch
        {
            "mon" => DayOfWeek.Monday, "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday, "thu" => DayOfWeek.Thursday,
            "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, "sun" => DayOfWeek.Sunday, _ => (DayOfWeek)(-1),
        };
        return (int)day >= 0;
    }

    private static bool TryParseTime(string? s, out TimeOnly t) => TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    private static bool TryParseDate(string? s, out DateOnly d) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    /// <summary>
    /// What is wrong with these settings, in plain words (empty when they are fine): every field is checked against
    /// <c>schema/settings.schema.json</c> and what the engine can use (spec §12). Saving refuses settings with problems.
    /// </summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (!string.IsNullOrWhiteSpace(TimeZone))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                p.Add($"Unknown time zone '{TimeZone}'.");
            }
        }
        if (Calendar is { } cal)
        {
            foreach (var d in cal.Days ?? [])
                if (!TryParseDay(d, out _)) p.Add($"'{d}' is not a day; use mon, tue, wed, thu, fri, sat or sun.");
            if (cal.Days is { } days && days.Distinct().Count() != days.Count) p.Add("A working day is listed twice.");
            if (cal.Hours is { } hours)
            {
                if (hours.Count != 2) p.Add("Working hours are a start and an end time.");
                else if (!TryParseTime(hours[0], out var s) || !TryParseTime(hours[1], out var e)) p.Add("Working hours must be times like 09:00 and 17:00 (HH:mm).");
                else if (e <= s) p.Add("Working hours must end after they start.");
            }
            foreach (var h in cal.Holidays ?? [])
                if (!TryParseDate(h, out _)) p.Add($"'{h}' is not a date (YYYY-MM-DD).");
        }
        if (Defaults is { } def)
        {
            if (def.Retry is { } r)
            {
                if (r.Max is < 0 or > 10) p.Add("Retries can be 0 to 10.");
                if (r.Backoff is not (null or "fixed" or "exponential")) p.Add($"'{r.Backoff}' is not a way to space retries (fixed or exponential).");
                if (r.Initial is not null && !IsoDuration.TryParse(r.Initial, out _)) p.Add($"'{r.Initial}' is not a duration.");
                if (r.MaxDelay is not null && !IsoDuration.TryParse(r.MaxDelay, out _)) p.Add($"'{r.MaxDelay}' is not a duration.");
            }
            foreach (var rem in def.Reminders ?? [])
                if (!IsoDuration.TryParse(rem, out var rd) || rd.Negative) p.Add($"'{rem}' is not a duration.");
            if (def.Concurrency is not (null or "perRecordSerial" or "parallel" or "skipIfRunning"))
                p.Add($"'{def.Concurrency}' is not a concurrency mode (perRecordSerial, parallel or skipIfRunning).");
            if (def.MaxChainDepth is < 1 or > 20) p.Add("The loop protection limit (maxChainDepth) must be 1 to 20.");
            if (def.ForEachMax is < 1 or > 500) p.Add("The most items a 'for each' covers (forEachMax) must be 1 to 500.");
        }
        foreach (var o in Webhooks?.AllowedOrigins ?? [])
            if (!Uri.TryCreate(o, UriKind.Absolute, out var ou) || ou.Scheme is not ("http" or "https") || ou.GetLeftPart(UriPartial.Authority) != o.TrimEnd('/') || o.EndsWith('/'))
                p.Add($"'{o}' is not an origin like https://api.example.com (scheme and host only).");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Webhooks?.Connections ?? [])
        {
            if (string.IsNullOrWhiteSpace(c.Key) || !System.Text.RegularExpressions.Regex.IsMatch(c.Key, "^[a-z][a-zA-Z0-9_]*$")) p.Add($"Connection key '{c.Key}' must start with a lower-case letter and use letters, digits and _.");
            else if (!keys.Add(c.Key)) p.Add($"Two connections are called '{c.Key}'.");
            if (!Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) p.Add($"Connection '{c.Key}' needs an http(s) address.");
            if (c.Auth is not (null or "none" or "secret")) p.Add($"Connection '{c.Key}': '{c.Auth}' is not an auth kind (none or secret).");
            if (c.Auth == "secret" && string.IsNullOrWhiteSpace(c.SecretName)) p.Add($"Connection '{c.Key}' uses a secret but names none.");
            if (c.Header is { } header && (header.Length == 0 || header.Any(ch => ch <= ' ' || ch >= 127 || "()<>@,;:\\\"/[]?={}".Contains(ch))))
                p.Add($"Connection '{c.Key}': '{header}' is not a header name.");
        }
        foreach (var (category, days) in Audit?.RetentionDays ?? [])
            if (days < 1) p.Add($"Audit entries of '{category}' must be kept at least 1 day.");
        foreach (var m in Audit?.MaskedFields ?? [])
            if (m.IndexOf('.') is var dot && (dot <= 0 || dot == m.Length - 1)) p.Add($"'{m}' is not a masked field like entity.field or input.key.");
        foreach (var pe in Paused ?? [])
            if (string.IsNullOrWhiteSpace(pe.Entity)) p.Add("A paused entry names no record type.");
        return p;
    }
}

/// <summary>The working calendar (spec §9).</summary>
public sealed class CalendarSettings
{
    /// <summary>Working days as <c>mon</c>…<c>sun</c>; null = Monday to Friday.</summary>
    public List<string>? Days { get; set; }
    /// <summary>Working hours as <c>["09:00", "17:00"]</c>.</summary>
    public List<string>? Hours { get; set; }
    /// <summary>Non-working dates as <c>YYYY-MM-DD</c>.</summary>
    public List<string>? Holidays { get; set; }
}

/// <summary>Defaults for steps and triggers.</summary>
public sealed class DefaultSettings
{
    /// <summary>Retry policy for calling steps without their own.</summary>
    public RetryPolicy? Retry { get; set; }
    /// <summary>Reminder offsets for tasks without their own, as ISO durations.</summary>
    public List<string>? Reminders { get; set; }
    /// <c>perRecordSerial</c>, <c>parallel</c> or <c>skipIfRunning</c>.
    public string? Concurrency { get; set; }
    /// <summary>Loop protection limit (default 5).</summary>
    public int? MaxChainDepth { get; set; }
    /// <summary>Most items a <c>forEach</c> covers (default 500).</summary>
    public int? ForEachMax { get; set; }
}

/// <summary>How tasks move between people.</summary>
public sealed class PeopleSettings
{
    /// <summary>Whether tasks may be delegated when a step does not say.</summary>
    public bool? Delegation { get; set; }
}

/// <summary>Settings of one notification channel.</summary>
public sealed class ChannelSettings
{
    /// <summary>False switches the channel off (notifications to it are skipped).</summary>
    public bool? Enabled { get; set; }
    /// <summary>Text appended to every message on the channel.</summary>
    public string? Footer { get; set; }
}

/// <summary>Where webhooks may go (spec §12).</summary>
public sealed class WebhookSettings
{
    /// <summary>Origins (<c>scheme://host[:port]</c>) a webhook <c>url</c> may use.</summary>
    public List<string>? AllowedOrigins { get; set; }
    /// <summary>Named connections steps refer to by key.</summary>
    public List<WebhookConnection>? Connections { get; set; }
}

/// <summary>A named webhook target whose secret stays with the host.</summary>
public sealed class WebhookConnection
{
    /// <summary>The key steps use.</summary>
    public string Key { get; set; } = "";
    /// <summary>The name people see.</summary>
    public string Label { get; set; } = "";
    /// <summary>The base address; step paths are appended and must stay on its host.</summary>
    public string BaseUrl { get; set; } = "";
    /// <c>none</c> or <c>secret</c>.
    public string? Auth { get; set; }
    /// <summary>The secret the host resolves, for <c>secret</c> auth.</summary>
    public string? SecretName { get; set; }
    /// <summary>The header carrying the secret as is; null = <c>Authorization: Bearer</c>.</summary>
    public string? Header { get; set; }
}

/// <summary>Audit retention and masking (spec §11).</summary>
public sealed class AuditSettings
{
    /// <summary>Days to keep entries, per category; older ones are purged with a checkpoint.</summary>
    public Dictionary<string, int>? RetentionDays { get; set; }
    /// <summary>Extra values to mask: <c>entity.field</c> and <c>input.key</c> entries.</summary>
    public List<string>? MaskedFields { get; set; }
}

/// <summary>An entity whose triggers are paused.</summary>
public sealed class PausedEntity
{
    /// <summary>The registry entity key.</summary>
    public string Entity { get; set; } = "";
}
