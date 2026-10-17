using System.Text.Json;
using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Lint;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

public sealed record DefinitionSummary(
    string Id, string Key, string Kind, string Title, string? Entity, string Status, int Version, bool HasDraftChanges,
    DateTimeOffset UpdatedAt, string? UpdatedBy, int RunningInstances);

public sealed record DefinitionDetail(DefinitionSummary Summary, JsonNode Draft, JsonNode? Published, IReadOnlyList<DefinitionVersionInfo> Versions, IReadOnlyList<LintProblem> Problems);

public sealed record DefinitionVersionInfo(int Version, string? Note, DateTimeOffset PublishedAt, string? PublishedBy, int Instances);

public sealed partial class WorkflowEngine
{
    /// <summary>Checks a definition against the spec, the registry, settings and the other published workflows.</summary>
    public Task<IReadOnlyList<LintProblem>> ValidateAsync(string json, CancellationToken ct = default) =>
        RunAsync(u => LintAsync(u, json, ct), ct);

    internal async Task<IReadOnlyList<LintProblem>> LintAsync(Uow u, string json, CancellationToken ct)
    {
        WorkflowDefinition def;
        try
        {
            def = Definitions.Parse(json);
        }
        catch (JsonException e)
        {
            return [new LintProblem("invalidJson", "error", null, $"The definition is not valid JSON: {e.Message}")];
        }
        var others = new List<WorkflowDefinition>();
        foreach (var d in await u.S.ListDefinitionsAsync(ct))
        {
            if (d.Id == def.Id || d.Version == 0 || d.Status == DefinitionStatus.Archived) continue;
            var v = await u.S.GetVersionAsync(d.Id, d.Version, ct);
            if (v is not null) others.Add(Definitions.Parse(v.Json));
        }
        return Linter.Lint(def, new LintContext(Registry, u.Settings, others));
    }

    public Task<IReadOnlyList<DefinitionSummary>> ListDefinitionsAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<DefinitionSummary>>(async u =>
        {
            var list = new List<DefinitionSummary>();
            foreach (var d in await u.S.ListDefinitionsAsync(ct)) list.Add(await SummaryAsync(u, d, ct));
            return list.OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);

    private static async Task<DefinitionSummary> SummaryAsync(Uow u, DefinitionRecord d, CancellationToken ct)
    {
        var running = await u.S.CountInstancesAsync(new InstanceQuery
        {
            DefinitionId = d.Id,
            Kinds = [InstanceKinds.Automation, InstanceKinds.Record],
            Statuses = [InstanceStatus.Running, InstanceStatus.Waiting, InstanceStatus.Incident, InstanceStatus.Queued, InstanceStatus.Active, InstanceStatus.OutOfSync],
        }, ct);
        return new DefinitionSummary(d.Id, d.Key, d.Kind, d.Title, d.Entity, d.Status, d.Version, d.HasDraftChanges, d.UpdatedAt, d.UpdatedBy, running);
    }

    public Task<DefinitionDetail> GetDefinitionAsync(string id, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var d = await RequireDefinitionAsync(u, id, ct);
            var perVersion = await u.S.CountInstancesByVersionAsync(id, ct);
            var versions = (await u.S.ListVersionsAsync(id, ct)).OrderByDescending(v => v.Version)
                .Select(v => new DefinitionVersionInfo(v.Version, v.Note, v.PublishedAt, v.PublishedBy, perVersion.GetValueOrDefault(v.Version)))
                .ToList();
            JsonNode? published = null;
            if (d.Version > 0) published = JsonNode.Parse((await u.S.GetVersionAsync(id, d.Version, ct))!.Json);
            var problems = await LintAsync(u, d.DraftJson, ct);
            return new DefinitionDetail(await SummaryAsync(u, d, ct), JsonNode.Parse(d.DraftJson)!, published, versions, problems);
        }, ct);

    public Task<JsonNode> GetVersionAsync(string id, int version, CancellationToken ct = default) =>
        RunAsync(async u => JsonNode.Parse((await u.S.GetVersionAsync(id, version, ct) ?? throw new WorkflowException("notFound", "That version does not exist.")).Json)!, ct);

    private static async Task<DefinitionRecord> RequireDefinitionAsync(Uow u, string id, CancellationToken ct) =>
        await u.S.GetDefinitionAsync(id, ct) ?? throw new WorkflowException("notFound", "That workflow does not exist.");

    /// <summary>Creates a draft from the builder's JSON. The definition's <c>id</c> and <c>key</c> must be unique.</summary>
    public Task<DefinitionSummary> CreateDefinitionAsync(string json, WorkflowActor actor, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var def = ParseForSave(json);
            if (await u.S.GetDefinitionAsync(def.Id, ct) is not null) throw new WorkflowException("duplicateId", "A workflow with this id already exists.");
            if (await u.S.GetDefinitionByKeyAsync(def.Key, ct) is not null) throw new WorkflowException("duplicateKey", $"A workflow with the key '{def.Key}' already exists.");
            var rec = new DefinitionRecord
            {
                Id = def.Id, Key = def.Key, Kind = def.Kind, Title = def.Title, Entity = def.Entity, Status = DefinitionStatus.Draft,
                DraftJson = Normalize(json), HasDraftChanges = true, CreatedAt = u.Now, CreatedBy = actor.Id, UpdatedAt = u.Now, UpdatedBy = actor.Id,
            };
            await u.S.AddDefinitionAsync(rec, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "definition", Action = "created", SubjectType = "definition", SubjectId = rec.Id, DefinitionId = rec.Id, Actor = actor,
                Data = new Dictionary<string, object?> { ["key"] = rec.Key, ["title"] = rec.Title, ["kind"] = rec.Kind },
            }, ct);
            return await SummaryAsync(u, rec, ct);
        }, ct);

    /// <summary>Saves the draft. Published versions are never changed by this.</summary>
    public Task<DefinitionSummary> SaveDraftAsync(string id, string json, WorkflowActor actor, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            var def = ParseForSave(json);
            if (def.Id != id) throw new WorkflowException("idMismatch", "The definition's id cannot change.");
            if (def.Kind != rec.Kind && rec.Version > 0) throw new WorkflowException("kindLocked", "A published workflow cannot change between automation and lifecycle.");
            if (def.Key != rec.Key)
            {
                if (rec.Version > 0) throw new WorkflowException("keyLocked", "A published workflow's key cannot change; other workflows and the API use it.");
                if (await u.S.GetDefinitionByKeyAsync(def.Key, ct) is not null) throw new WorkflowException("duplicateKey", $"A workflow with the key '{def.Key}' already exists.");
            }
            var normalized = Normalize(json);
            var changed = normalized != rec.DraftJson;
            rec.DraftJson = normalized;
            rec.Key = def.Key;
            rec.Kind = def.Kind;
            rec.Title = def.Title;
            rec.Entity = def.Entity;
            rec.UpdatedAt = u.Now;
            rec.UpdatedBy = actor.Id;
            if (rec.Version > 0)
            {
                var pub = await u.S.GetVersionAsync(id, rec.Version, ct);
                rec.HasDraftChanges = pub is null || Hash(normalized) != pub.Hash;
            }
            await u.S.UpdateDefinitionAsync(rec, ct);
            if (changed)
            {
                await u.AuditAsync(new AuditEntry { Category = "definition", Action = "draftSaved", SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor }, ct);
            }
            return await SummaryAsync(u, rec, ct);
        }, ct);

    private static WorkflowDefinition ParseForSave(string json)
    {
        WorkflowDefinition def;
        try
        {
            def = Definitions.Parse(json);
        }
        catch (JsonException e)
        {
            throw new WorkflowException("invalidJson", $"The definition is not valid JSON: {e.Message}");
        }
        if (string.IsNullOrWhiteSpace(def.Id) || string.IsNullOrWhiteSpace(def.Key) || string.IsNullOrWhiteSpace(def.Title))
            throw new WorkflowException("invalidDefinition", "A workflow needs an id, a key and a title.");
        if (def.Kind is not (WorkflowKinds.Automation or WorkflowKinds.Lifecycle))
            throw new WorkflowException("invalidDefinition", "A workflow is either an automation or a lifecycle.");
        if (json.Length > 2_000_000) throw new WorkflowException("tooLarge", "The workflow is too large.");
        return def;
    }

    private static string Normalize(string json) => JsonNode.Parse(json)!.ToJsonString();

    internal static string Hash(string json) => AuditChain.Sha256Hex(Values.Canonical(Values.FromJson(json)));

    /// <summary>Publishes the draft as a new immutable version. Lint errors block publishing.</summary>
    public async Task<DefinitionSummary> PublishAsync(string id, string? note, WorkflowActor actor, CancellationToken ct = default)
    {
        var result = await PublishCoreAsync(id, note, actor, ct);
        // Record writes right after publishing must already see a new lifecycle's field ownership.
        await RefreshOwnershipAsync(ct);
        return result;
    }

    private Task<DefinitionSummary> PublishCoreAsync(string id, string? note, WorkflowActor actor, CancellationToken ct) =>
        RunAsync(u => PublishInUnitAsync(u, id, note, actor, ct), ct);

    /// <summary>Publishes the draft inside an open unit of work (lint, new version, trigger index, audit).</summary>
    private async Task<DefinitionSummary> PublishInUnitAsync(Uow u, string id, string? note, WorkflowActor actor, CancellationToken ct)
    {
        var rec = await RequireDefinitionAsync(u, id, ct);
        var problems = await LintAsync(u, rec.DraftJson, ct);
        var errors = problems.Where(p => p.Severity == "error").ToList();
        if (errors.Count > 0) throw new WorkflowException("lintErrors", $"Fix {errors.Count} problem(s) before publishing.", errors);
        var def = Definitions.Parse(rec.DraftJson);
        JsonNode? previous = null;
        if (rec.Version > 0) previous = JsonNode.Parse((await u.S.GetVersionAsync(id, rec.Version, ct))!.Json);
        var version = rec.Version + 1;
        var draft = JsonNode.Parse(rec.DraftJson)!.AsObject();
        draft["revision"] = version;
        var json = draft.ToJsonString();
        await u.S.AddVersionAsync(new DefinitionVersionRecord
        {
            DefinitionId = id, Version = version, Key = rec.Key, Json = json, Hash = Hash(json), Note = note, PublishedAt = u.Now, PublishedBy = actor.Id,
        }, ct);
        rec.DraftJson = json;
        rec.Version = version;
        rec.Status = DefinitionStatus.Published;
        rec.HasDraftChanges = false;
        rec.UpdatedAt = u.Now;
        rec.UpdatedBy = actor.Id;
        await u.S.UpdateDefinitionAsync(rec, ct);
        await IndexTriggersAsync(u, rec, Definitions.Parse(json), version, ct);
        var diff = previous is null ? null : DefinitionDiff.Describe(Definitions.Parse(previous.ToJsonString()), def);
        await u.AuditAsync(new AuditEntry
        {
            Category = "definition", Action = "published", SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor,
            Data = new Dictionary<string, object?> { ["version"] = (double)version, ["note"] = note, ["diff"] = diff },
        }, ct);
        InvalidateOwnership();
        return await SummaryAsync(u, rec, ct);
    }

    /// <summary>Builds the trigger index rows for a published version (and timers for schedules and date triggers).</summary>
    private async Task IndexTriggersAsync(Uow u, DefinitionRecord rec, WorkflowDefinition def, int version, CancellationToken ct)
    {
        var rows = new List<TriggerRecord>();
        var trig = def.Trigger;
        // Changes are compared with the first publish, not this version's: events still in the outbox when a
        // new version is published belong to the workflow and must not be dropped (spec §5).
        var firstPublished = version == 1 ? u.Now : (await u.S.GetVersionAsync(rec.Id, 1, ct))?.PublishedAt ?? u.Now;
        if (def.IsLifecycle && def.Lifecycle is { } lc)
        {
            var initial = lc.States.FirstOrDefault(s => s.IsInitial)?.Key;
            rows.Add(new TriggerRecord
            {
                Id = NewId(), DefinitionId = rec.Id, DefinitionKey = rec.Key, Version = version, Type = "lifecycle", Entity = def.Entity, Field = lc.Field, PublishedAt = firstPublished,
                Data = Values.Serialize(new Dictionary<string, object?> { ["mode"] = lc.Mode ?? "enforce", ["initial"] = initial }),
            });
        }
        else if (trig is not null)
        {
            rows.Add(new TriggerRecord
            {
                Id = NewId(), DefinitionId = rec.Id, DefinitionKey = rec.Key, Version = version, Type = trig.Type, Entity = def.Entity, Field = trig.Field, PublishedAt = firstPublished,
                Lifecycle = trig.Lifecycle, Signal = trig.Signal, Data = trig.Cron is null ? null : Values.Serialize(new Dictionary<string, object?> { ["cron"] = trig.Cron }),
            });
        }
        await u.S.ReplaceTriggersAsync(rec.Id, rows, ct);

        // Re-plan schedule and date timers for the new version.
        foreach (var b in await u.S.FindDefinitionBookmarksAsync(rec.Id, BookmarkKinds.Schedule, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        if (trig?.Type == TriggerTypes.Schedule && trig.Cron is not null && Cron.TryParse(trig.Cron, out var cron, out _))
        {
            var next = cron!.Next(u.Now, u.Calendar.Zone);
            if (next is not null)
                await u.S.AddBookmarkAsync(new BookmarkRecord { Id = NewId(), Kind = BookmarkKinds.Schedule, Purpose = "schedule", DefinitionId = rec.Id, DueAt = next, CreatedAt = u.Now }, ct);
        }
        if (trig?.Type == TriggerTypes.DateReached) await PlanDateTriggersForAllAsync(u, new CompiledDefinition(def, rec.Id, version), ct);
        else foreach (var b in await u.S.FindDefinitionBookmarksAsync(rec.Id, BookmarkKinds.DateReached, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
    }

    public Task<DefinitionSummary> ArchiveAsync(string id, WorkflowActor actor, CancellationToken ct = default) =>
        SetStatusAsync(id, DefinitionStatus.Archived, "archived", actor, ct);

    public Task<DefinitionSummary> RestoreAsync(string id, WorkflowActor actor, CancellationToken ct = default) =>
        SetStatusAsync(id, null, "restored", actor, ct);

    private async Task<DefinitionSummary> SetStatusAsync(string id, string? status, string action, WorkflowActor actor, CancellationToken ct)
    {
        var result = await SetStatusCoreAsync(id, status, action, actor, ct);
        await RefreshOwnershipAsync(ct);
        return result;
    }

    private Task<DefinitionSummary> SetStatusCoreAsync(string id, string? status, string action, WorkflowActor actor, CancellationToken ct) =>
        RunAsync(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            rec.Status = status ?? (rec.Version > 0 ? DefinitionStatus.Published : DefinitionStatus.Draft);
            rec.UpdatedAt = u.Now;
            rec.UpdatedBy = actor.Id;
            await u.S.UpdateDefinitionAsync(rec, ct);
            if (status == DefinitionStatus.Archived)
            {
                // An archived workflow starts nothing new; running instances finish on their version.
                await u.S.ReplaceTriggersAsync(id, [], ct);
                foreach (var b in await u.S.FindDefinitionBookmarksAsync(id, BookmarkKinds.Schedule, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
                foreach (var b in await u.S.FindDefinitionBookmarksAsync(id, BookmarkKinds.DateReached, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
            }
            else if (rec.Version > 0)
            {
                var v = await u.S.GetVersionAsync(id, rec.Version, ct);
                await IndexTriggersAsync(u, rec, Definitions.Parse(v!.Json), rec.Version, ct);
            }
            await u.AuditAsync(new AuditEntry { Category = "definition", Action = action, SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor }, ct);
            InvalidateOwnership();
            return await SummaryAsync(u, rec, ct);
        }, ct);

    /// <summary>
    /// Publishes a copy of an earlier version as the newest version, in one unit of work: if the copy no
    /// longer passes lint (the registry changed), nothing changes, the draft included.
    /// </summary>
    public async Task<DefinitionSummary> RollbackAsync(string id, int version, WorkflowActor actor, CancellationToken ct = default)
    {
        var result = await RunAsync(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            var v = await u.S.GetVersionAsync(id, version, ct) ?? throw new WorkflowException("notFound", "That version does not exist.");
            rec.DraftJson = v.Json;
            rec.HasDraftChanges = true;
            await u.S.UpdateDefinitionAsync(rec, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "definition", Action = "rolledBack", SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor,
                Data = new Dictionary<string, object?> { ["toVersion"] = (double)version },
            }, ct);
            return await PublishInUnitAsync(u, id, $"Rolled back to version {version}", actor, ct);
        }, ct);
        await RefreshOwnershipAsync(ct);
        return result;
    }

    /// <summary>Deletes a workflow that was never published.</summary>
    public Task DeleteDefinitionAsync(string id, WorkflowActor actor, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            if (rec.Version > 0) throw new WorkflowException("published", "A published workflow cannot be deleted; archive it instead.");
            await u.S.DeleteDefinitionAsync(id, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "definition", Action = "deleted", SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor,
                Data = new Dictionary<string, object?> { ["key"] = rec.Key },
            }, ct);
        }, ct);

    /// <summary>Plain-English changes between the published version and the draft.</summary>
    public Task<IReadOnlyList<string>> DiffDraftAsync(string id, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<string>>(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            if (rec.Version == 0) return ["Not published yet."];
            var pub = await u.S.GetVersionAsync(id, rec.Version, ct);
            return DefinitionDiff.Describe(Definitions.Parse(pub!.Json), Definitions.Parse(rec.DraftJson));
        }, ct);
}
