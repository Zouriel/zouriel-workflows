using System.Security.Cryptography;
using System.Text;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Audit;

/// <summary>An audit entry before it is chained (spec §11).</summary>
internal sealed record AuditEntry
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

/// <summary>
/// Collects the audit entries of one unit of work and chains them right before it commits. The chain
/// head is locked only for that last moment (<see cref="FlushAsync"/>), so units that call slow webhooks
/// or actions do not queue behind each other.
/// </summary>
internal sealed class AuditWriter(IWorkflowSession session, string? tenant, Func<DateTimeOffset> now)
{
    public const string GenesisHash = AuditChain.GenesisHash;

    private readonly List<AuditRecord> _pending = [];

    /// <summary>Entries written in this unit (chained once <see cref="FlushAsync"/> ran).</summary>
    public IReadOnlyList<AuditRecord> Pending => _pending;

    /// <summary>Queues an entry; its sequence number and hash are assigned when the unit commits.</summary>
    public void Write(AuditEntry e) => _pending.Add(new AuditRecord
    {
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
    });

    /// <summary>Locks the chain head, links the queued entries to it, stores them and moves the head.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;
        var head = await session.GetAuditHeadAsync(tenant, ct);
        var seq = head?.Seq ?? 0;
        var prev = head?.Hash ?? GenesisHash;
        foreach (var rec in _pending)
        {
            rec.Seq = ++seq;
            rec.PrevHash = prev;
            rec.Hash = AuditChain.ComputeHash(rec);
            prev = rec.Hash;
            await session.AddAuditAsync(rec, ct);
        }
        await session.SetAuditHeadAsync(tenant, new AuditHead(seq, prev), ct);
        _pending.Clear();
    }

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
}

/// <summary>The audit hash chain (spec §11): what is hashed and how.</summary>
public static class AuditChain
{
    /// <summary>The <c>prevHash</c> of the first entry of a chain.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

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

    /// <summary><c>sha256_hex(prevHash + "\n" + jcs(entry))</c>.</summary>
    public static string ComputeHash(AuditRecord r) => Sha256Hex(r.PrevHash + "\n" + Values.Canonical(HashedObject(r)));

    /// <summary>Lower-case hex SHA-256 of a UTF-8 string.</summary>
    public static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>The result of checking the audit chain.</summary>
/// <param name="Valid">True when every entry checked out.</param>
/// <param name="Checked">How many entries were checked (up to and including a broken one).</param>
/// <param name="BrokenAtSeq">The first sequence number that failed, if any.</param>
/// <param name="Problem">What was wrong, in plain words.</param>
/// <param name="PurgedGaps">Runs of missing entries that recorded purges explain.</param>
public sealed record AuditVerification(bool Valid, long Checked, long? BrokenAtSeq, string? Problem, IReadOnlyList<(long From, long To)> PurgedGaps);

/// <summary>
/// Checks an audit chain entry by entry (oldest first), so it can run over pages without holding the
/// whole log in memory: each entry's own hash, each link to the entry before it, strictly increasing
/// sequence numbers, and that every gap is exactly covered by the ranges purges recorded.
/// </summary>
public sealed class AuditChainVerifier
{
    private readonly List<SeqRange> _purged;
    private readonly List<(long From, long To)> _gaps = [];
    private readonly long _firstSeq;
    private AuditRecord? _prev;

    /// <summary>
    /// Starts a check. <paramref name="purged"/> are the ranges audit purge checkpoints recorded;
    /// <paramref name="firstSeq"/> is where the check starts (entries before it must be purged ones).
    /// </summary>
    public AuditChainVerifier(IEnumerable<SeqRange>? purged = null, long firstSeq = 1)
    {
        _purged = (purged ?? []).OrderBy(r => r.From).ToList();
        _firstSeq = firstSeq;
    }

    /// <summary>How many entries were fed so far.</summary>
    public long Checked { get; private set; }

    /// <summary>The first failure, once one is found; later entries are ignored.</summary>
    public AuditVerification? Failure { get; private set; }

    /// <summary>Checks the next entry. Returns false once the chain is broken.</summary>
    public bool Add(AuditRecord e)
    {
        if (Failure is not null) return false;
        Checked++;
        if (AuditChain.ComputeHash(e) != e.Hash) return Fail(e.Seq, "Entry content does not match its hash.");
        if (_prev is null)
        {
            if (e.Seq == 1 && e.PrevHash != AuditChain.GenesisHash) return Fail(e.Seq, "The first entry does not start the chain.");
            if (e.Seq > _firstSeq && !Covered(_firstSeq, e.Seq - 1)) return Fail(_firstSeq, $"Entries {_firstSeq}–{e.Seq - 1} are missing and no purge covers them.");
        }
        else if (e.Seq <= _prev.Seq)
        {
            return Fail(e.Seq, $"Sequence number {e.Seq} is repeated or out of order.");
        }
        else if (e.Seq == _prev.Seq + 1)
        {
            if (e.PrevHash != _prev.Hash) return Fail(e.Seq, "Entry does not link to the entry before it.");
        }
        else
        {
            var from = _prev.Seq + 1;
            var to = e.Seq - 1;
            if (!Covered(from, to)) return Fail(from, $"Entries {from}–{to} are missing and no purge covers them.");
            _gaps.Add((from, to));
        }
        _prev = e;
        return true;
    }

    /// <summary>
    /// The verdict after the last entry. With the chain's recorded <paramref name="head"/>, entries missing
    /// at the end (after the last one read) must be purged ones too, and the head must match the last entry.
    /// </summary>
    public AuditVerification Result(AuditHead? head = null)
    {
        if (Failure is not null || head is null) return Failure ?? new AuditVerification(true, Checked, null, null, _gaps);
        var last = _prev?.Seq ?? _firstSeq - 1;
        if (head.Seq < last) return new AuditVerification(false, Checked, head.Seq, "The chain head is behind its newest entry.", _gaps);
        if (head.Seq == last && _prev is not null && head.Hash != _prev.Hash) return new AuditVerification(false, Checked, last, "The newest entry does not match the chain head.", _gaps);
        if (head.Seq > last && !Covered(last + 1, head.Seq)) return new AuditVerification(false, Checked, last + 1, $"Entries {last + 1}–{head.Seq} are missing and no purge covers them.", _gaps);
        return new AuditVerification(true, Checked, null, null, _gaps);
    }

    private bool Fail(long seq, string problem)
    {
        Failure = new AuditVerification(false, Checked, seq, problem, _gaps);
        return false;
    }

    /// <summary>True when every sequence number in [from, to] lies in a recorded purge range.</summary>
    private bool Covered(long from, long to)
    {
        var next = from;
        foreach (var r in _purged)
        {
            if (r.To < next) continue;
            if (r.From > next) return false;
            next = r.To + 1;
            if (next > to) return true;
        }
        return next > to;
    }
}

/// <summary>Checks a list of audit entries in one go (see <see cref="AuditChainVerifier"/>).</summary>
public static class AuditVerifier
{
    /// <summary>Checks entries (oldest first) against the ranges recorded by purges.</summary>
    public static AuditVerification Verify(IEnumerable<AuditRecord> entries, IEnumerable<SeqRange>? purged = null)
    {
        var v = new AuditChainVerifier(purged);
        foreach (var e in entries)
            if (!v.Add(e)) break;
        return v.Result();
    }
}
