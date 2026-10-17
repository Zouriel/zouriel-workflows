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
    /// <summary>Delegation and escalation defaults.</summary>
    public PeopleSettings? People { get; set; }
    /// <summary>Per notification channel (by registry key): enabled, sender, footer.</summary>
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

    /// <summary>The calendar durations are added with (working time only when a calendar is set).</summary>
    public WorkCalendar WorkCalendar()
    {
        var zone = Zone();
        if (Calendar is null) return new WorkCalendar(zone, hasWorkingTime: false);
        var days = Calendar.Days?.Select(ParseDay).ToList();
        TimeOnly? start = null, end = null;
        if (Calendar.Hours is { Count: 2 })
        {
            start = TimeOnly.ParseExact(Calendar.Hours[0], "HH:mm", CultureInfo.InvariantCulture);
            end = TimeOnly.ParseExact(Calendar.Hours[1], "HH:mm", CultureInfo.InvariantCulture);
        }
        var holidays = Calendar.Holidays?.Select(h => DateOnly.ParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return new WorkCalendar(zone, days, start, end, holidays);
    }

    private static DayOfWeek ParseDay(string d) => d switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };
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
    /// <summary>Due time for tasks, as an ISO duration.</summary>
    public string? TaskDue { get; set; }
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
    /// <summary>Who escalations go to when a step does not say.</summary>
    public List<Assignee>? EscalationDefault { get; set; }
}

/// <summary>Settings of one notification channel.</summary>
public sealed class ChannelSettings
{
    /// <summary>False switches the channel off (notifications to it are skipped).</summary>
    public bool? Enabled { get; set; }
    /// <summary>The sender the host should use, e.g. a from-address.</summary>
    public string? Sender { get; set; }
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
