using System.Globalization;
using System.Text.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Settings;

/// <summary>App-wide workflow settings (spec §12), edited in <c>ui-workflow-global-settings</c>.</summary>
public sealed class GlobalSettings
{
    public int Version { get; set; } = 1;
    public string? TimeZone { get; set; }
    public CalendarSettings? Calendar { get; set; }
    public DefaultSettings? Defaults { get; set; }
    public PeopleSettings? People { get; set; }
    public Dictionary<string, ChannelSettings>? Channels { get; set; }
    public WebhookSettings? Webhooks { get; set; }
    public AuditSettings? Audit { get; set; }
    public List<PausedEntity>? Paused { get; set; }

    public static GlobalSettings Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new GlobalSettings() : JsonSerializer.Deserialize<GlobalSettings>(json, Definitions.JsonOptions) ?? new GlobalSettings();

    public string ToJson() => JsonSerializer.Serialize(this, Definitions.JsonOptions);

    public int MaxChainDepth => Defaults?.MaxChainDepth ?? 5;
    public int ForEachMax => Defaults?.ForEachMax ?? 500;
    public string DefaultConcurrency => Defaults?.Concurrency ?? "perRecordSerial";

    public bool IsPaused(string? entity) => entity is not null && Paused?.Any(p => p.Entity == entity) == true;

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

public sealed class CalendarSettings
{
    public List<string>? Days { get; set; }
    public List<string>? Hours { get; set; }
    public List<string>? Holidays { get; set; }
}

public sealed class DefaultSettings
{
    public RetryPolicy? Retry { get; set; }
    public string? TaskDue { get; set; }
    public List<string>? Reminders { get; set; }
    public string? Concurrency { get; set; }
    public int? MaxChainDepth { get; set; }
    public int? ForEachMax { get; set; }
}

public sealed class PeopleSettings
{
    public bool? Delegation { get; set; }
    public List<Assignee>? EscalationDefault { get; set; }
}

public sealed class ChannelSettings
{
    public bool? Enabled { get; set; }
    public string? Sender { get; set; }
    public string? Footer { get; set; }
}

public sealed class WebhookSettings
{
    public List<string>? AllowedOrigins { get; set; }
    public List<WebhookConnection>? Connections { get; set; }
}

public sealed class WebhookConnection
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? Auth { get; set; }
    public string? SecretName { get; set; }
    public string? Header { get; set; }
}

public sealed class AuditSettings
{
    public Dictionary<string, int>? RetentionDays { get; set; }
    public List<string>? MaskedFields { get; set; }
}

public sealed class PausedEntity
{
    public string Entity { get; set; } = "";
}
