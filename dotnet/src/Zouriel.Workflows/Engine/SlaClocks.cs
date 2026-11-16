using System.Globalization;
using System.Text.Json.Nodes;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Engine;

/// <summary>
/// A kept SLA clock (spec §6 <c>sla.pauseOnStatus</c>, <c>pauseOnEvent</c>): what a record instance remembers about a state's
/// time limit while the timer is not planned. One per owning state, kept in the record instance's <c>Data</c> JSON as
/// <c>{"sla":{"STATE_ID":{...}}}</c>, so no column is needed and a record that never pauses keeps <c>{}</c>.
/// </summary>
internal sealed class SlaClock
{
    public const string Status = "status";
    public const string Event = "event";

    /// <summary>Time left when the clock was paused (working time when <see cref="Working"/>), in milliseconds.</summary>
    public long LeftMs { get; set; }
    /// <summary>Whether <see cref="LeftMs"/> is working time (the SLA's calendar was the working one and the settings have one).</summary>
    public bool Working { get; set; }
    /// <summary>Why the clock is paused: <c>status</c> (the record is in a listed state) and/or <c>event</c>. Empty only for a clock about to resume.</summary>
    public List<string> Reasons { get; set; } = [];
    /// <summary>The SLA already ran in this stay: no clock exists and none is planned again.</summary>
    public bool Ran { get; set; }
    /// <summary>A clock that was running when its lifecycle was archived: the instant it was due; restoring plans it there.</summary>
    public DateTimeOffset? Due { get; set; }

    public void AddReason(string reason)
    {
        if (!Reasons.Contains(reason)) Reasons.Add(reason);
        Reasons = Reasons.OrderBy(r => r == Status ? 0 : 1).ToList();
    }

    public TimeSpan Left => TimeSpan.FromMilliseconds(LeftMs);
}

/// <summary>Reads and writes the kept clocks of a record instance.</summary>
internal static class SlaClocks
{
    private const string Key = "sla";

    public static Dictionary<string, SlaClock> Read(InstanceRecord record)
    {
        var result = new Dictionary<string, SlaClock>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(record.Data) || record.Data == "{}") return result;
        if (JsonNode.Parse(record.Data) is not JsonObject root || root[Key] is not JsonObject clocks) return result;
        foreach (var (stateId, node) in clocks)
        {
            if (node is not JsonObject o) continue;
            var c = new SlaClock
            {
                LeftMs = o["leftMs"]?.GetValue<long>() ?? 0,
                Working = o["working"]?.GetValue<bool>() ?? false,
                Ran = o["ran"]?.GetValue<bool>() ?? false,
                Reasons = (o["reasons"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToList() ?? [],
            };
            if (o["due"]?.GetValue<string>() is { } due) c.Due = DateTimeOffset.Parse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            result[stateId] = c;
        }
        return result;
    }

    /// <summary>Stores the clocks (none: the record's Data goes back to <c>{}</c>). Does not save the instance.</summary>
    public static void Write(InstanceRecord record, Dictionary<string, SlaClock> clocks)
    {
        var root = string.IsNullOrWhiteSpace(record.Data) || record.Data == "{}" ? new JsonObject() : JsonNode.Parse(record.Data) as JsonObject ?? new JsonObject();
        root.Remove(Key);
        if (clocks.Count > 0)
        {
            var o = new JsonObject();
            foreach (var (stateId, c) in clocks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var n = new JsonObject();
                if (c.Ran) n["ran"] = true;
                else if (c.Due is { } due) n["due"] = Values.FormatInstant(due);
                else
                {
                    n["leftMs"] = c.LeftMs;
                    n["working"] = c.Working;
                }
                if (c.Reasons.Count > 0) n["reasons"] = new JsonArray(c.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
                o[stateId] = n;
            }
            root[Key] = o;
        }
        record.Data = root.Count == 0 ? "{}" : root.ToJsonString();
    }

    /// <summary>Drops every kept clock of the record (force-state, reset, out-of-sync, migrate, cancel).</summary>
    public static void Clear(InstanceRecord record)
    {
        if (record.Data.Length > 2 && Read(record).Count > 0) Write(record, []);
    }
}
