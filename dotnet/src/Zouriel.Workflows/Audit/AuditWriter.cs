using System.Security.Cryptography;
using System.Text;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Audit;

/// <summary>An audit entry before it is chained (spec §11).</summary>
public sealed class AuditEntry
{
    public required string Category { get; init; }
    public required string Action { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public WorkflowActor Actor { get; init; } = WorkflowActor.System;
    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    public string? InstanceId { get; init; }
    public string? DefinitionId { get; init; }
    public object? Before { get; init; }
    public object? After { get; init; }
    public object? Data { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
}

/// <summary>Writes hash-chained audit entries inside the current unit of work.</summary>
public sealed class AuditWriter(IWorkflowSession session, string? tenant, Func<DateTimeOffset> now)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private AuditRecord? _head;
    private bool _headLoaded;

    public List<AuditRecord> Written { get; } = [];

    public async Task<AuditRecord> WriteAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (!_headLoaded)
        {
            _head = await session.GetAuditHeadAsync(tenant, ct);
            _headLoaded = true;
        }
        var rec = new AuditRecord
        {
            Seq = (_head?.Seq ?? 0) + 1,
            Id = Guid.NewGuid().ToString("N"),
            At = TruncateToMilliseconds(now()),
            Tenant = tenant,
            ActorType = e.Actor.Type,
            ActorId = e.Actor.Id,
            ActorName = e.Actor.Name,
            Category = e.Category,
            Action = e.Action,
            SubjectType = e.SubjectType,
            SubjectId = e.SubjectId,
            Entity = e.Entity,
            EntityId = e.EntityId,
            InstanceId = e.InstanceId,
            DefinitionId = e.DefinitionId,
            Before = e.Before is null ? null : Values.Serialize(e.Before),
            After = e.After is null ? null : Values.Serialize(e.After),
            Data = e.Data is null ? null : Values.Serialize(e.Data),
            CorrelationId = e.CorrelationId ?? e.InstanceId ?? e.SubjectId,
            CausationId = e.CausationId,
            PrevHash = _head?.Hash ?? GenesisHash,
        };
        rec.Hash = ComputeHash(rec);
        await session.AddAuditAsync(rec, ct);
        _head = rec;
        Written.Add(rec);
        return rec;
    }

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>The object that is hashed: every field except <c>prevHash</c> and <c>hash</c>, nulls left out.</summary>
    public static Dictionary<string, object?> HashedObject(AuditRecord r)
    {
        var actor = new Dictionary<string, object?> { ["type"] = r.ActorType };
        if (r.ActorId is not null) actor["id"] = r.ActorId;
        if (r.ActorName is not null) actor["name"] = r.ActorName;
        var subject = new Dictionary<string, object?> { ["type"] = r.SubjectType, ["id"] = r.SubjectId };
        if (r.Entity is not null) subject["entity"] = r.Entity;
        if (r.EntityId is not null) subject["entityId"] = r.EntityId;
        var o = new Dictionary<string, object?>
        {
            ["seq"] = (double)r.Seq,
            ["id"] = r.Id,
            ["at"] = Values.FormatInstant(r.At),
            ["actor"] = actor,
            ["category"] = r.Category,
            ["action"] = r.Action,
            ["subject"] = subject,
            ["correlationId"] = r.CorrelationId,
        };
        if (r.Tenant is not null) o["tenant"] = r.Tenant;
        if (r.InstanceId is not null) o["instanceId"] = r.InstanceId;
        if (r.DefinitionId is not null) o["definitionId"] = r.DefinitionId;
        if (r.Before is not null) o["before"] = Values.FromJson(r.Before);
        if (r.After is not null) o["after"] = Values.FromJson(r.After);
        if (r.Data is not null) o["data"] = Values.FromJson(r.Data);
        if (r.CausationId is not null) o["causationId"] = r.CausationId;
        return o;
    }

    public static string ComputeHash(AuditRecord r) => Sha256Hex(r.PrevHash + "\n" + Values.Canonical(HashedObject(r)));

    public static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>Masks the values of sensitive keys with <c>"***"</c>.</summary>
    public static Dictionary<string, object?> Mask(IReadOnlyDictionary<string, object?> values, ISet<string> sensitive) =>
        values.ToDictionary(kv => kv.Key, kv => sensitive.Contains(kv.Key) && kv.Value is not null ? "***" : kv.Value);
}

/// <summary>The result of checking the audit chain.</summary>
public sealed record AuditVerification(bool Valid, long Checked, long? BrokenAtSeq, string? Problem, IReadOnlyList<(long From, long To)> PurgedGaps);

public static class AuditVerifier
{
    /// <summary>Checks entries (oldest first): each entry's own hash, and each link to the entry before it.</summary>
    public static AuditVerification Verify(IReadOnlyList<AuditRecord> entries, IReadOnlyList<(long From, long To)>? purged = null)
    {
        var gaps = new List<(long, long)>();
        AuditRecord? prev = null;
        foreach (var e in entries)
        {
            if (ComputeOk(e) is false) return new AuditVerification(false, gaps.Count, e.Seq, "Entry content does not match its hash.", gaps);
            if (prev is not null)
            {
                if (e.Seq == prev.Seq + 1)
                {
                    if (e.PrevHash != prev.Hash) return new AuditVerification(false, entries.Count, e.Seq, "Entry does not link to the entry before it.", gaps);
                }
                else
                {
                    var from = prev.Seq + 1;
                    var to = e.Seq - 1;
                    var covered = purged?.Any(p => p.From <= from && p.To >= to) == true;
                    if (!covered) return new AuditVerification(false, entries.Count, from, $"Entries {from}–{to} are missing and no purge covers them.", gaps);
                    gaps.Add((from, to));
                }
            }
            else if (e.Seq == 1 && e.PrevHash != AuditWriter.GenesisHash)
            {
                return new AuditVerification(false, 1, e.Seq, "The first entry does not start the chain.", gaps);
            }
            prev = e;
        }
        return new AuditVerification(true, entries.Count, null, null, gaps);
    }

    private static bool ComputeOk(AuditRecord e) => AuditWriter.ComputeHash(e) == e.Hash;
}
