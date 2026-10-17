using System.Text.Json;
using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Lint;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

/// <summary>A workflow definition in lists (builder home page, API).</summary>
/// <param name="Id">The definition id.</param>
/// <param name="Key">Its key (unique; other workflows and the API use it).</param>
/// <param name="Kind"><c>automation</c> or <c>lifecycle</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="Entity">The record type it is about, if any.</param>
/// <param name="Status"><c>draft</c>, <c>published</c> or <c>archived</c> (spec §13).</param>
/// <param name="Version">The latest published version (0 when never published).</param>
/// <param name="HasDraftChanges">True when the draft differs from the latest published version.</param>
/// <param name="UpdatedAt">When the draft or status last changed.</param>
/// <param name="UpdatedBy">Who changed it.</param>
/// <param name="RunningInstances">Unfinished automation and record instances of any version.</param>
public sealed record DefinitionSummary(
    string Id, string Key, string Kind, string Title, string? Entity, string Status, int Version, bool HasDraftChanges,
    DateTimeOffset UpdatedAt, string? UpdatedBy, int RunningInstances);

/// <summary>A definition with its draft, latest published version, version history and the draft's lint problems.</summary>
/// <param name="Summary">The list summary.</param>
/// <param name="Draft">The draft JSON.</param>
/// <param name="Published">The latest published version's JSON, or null.</param>
/// <param name="Versions">Published versions, newest first, with instance counts.</param>
/// <param name="Problems">Lint problems of the draft (spec §13).</param>
public sealed record DefinitionDetail(DefinitionSummary Summary, JsonNode Draft, JsonNode? Published, IReadOnlyList<DefinitionVersionInfo> Versions, IReadOnlyList<LintProblem> Problems);

/// <summary>One published version of a definition.</summary>
/// <param name="Version">The version number.</param>
/// <param name="Note">The publish note.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="PublishedBy">Who published it.</param>
/// <param name="Instances">How many automation and record instances are pinned to it.</param>
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
        // Published versions never change, so their parsed form comes from the engine's cache.
        var others = new List<WorkflowDefinition>();
        foreach (var d in await u.S.ListDefinitionsAsync(ct))
        {
            if (d.Id == def.Id || d.Version == 0 || d.Status == DefinitionStatus.Archived) continue;
            others.Add((await CompiledAsync(u, d.Id, d.Version, ct)).Definition);
        }
        return Linter.Lint(def, new LintContext(Registry, u.Settings, others));
    }

    /// <summary>All definitions, by title, with their running instance counts.</summary>
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

    /// <summary>One definition with its draft, published version, history and draft lint problems. Throws <c>notFound</c>.</summary>
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

    /// <summary>The JSON of one published version (immutable). Throws <c>notFound</c>.</summary>
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

    // ── Permissions ───────────────────────────────────────────────────────

    /// <summary>
    /// Changes who may do steps of a published workflow and publishes it straight away (spec §13
    /// permissions): the new version is the current published version plus only these changes, so
    /// unpublished draft edits are neither published nor lost (the same changes are applied to the draft
    /// too). A node is a transition id (its <c>who</c>), <c>reset</c> (the lifecycle reset's <c>who</c>),
    /// <c>trigger</c> (a manual trigger's <c>who</c>) or an approval/task step id (its <c>assignees</c>).
    /// Audited as <c>definition.permissionsChanged</c>.
    /// </summary>
    public async Task<DefinitionSummary> UpdatePermissionsAsync(string id, IReadOnlyList<(string Node, JsonArray Who)> changes, string? note, WorkflowActor actor,
        CancellationToken ct = default)
    {
        if (changes.Count == 0) throw new WorkflowException("invalidInput", "Say which permissions change.");
        var result = await RunAsync(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            if (rec.Version == 0 || rec.Status == DefinitionStatus.Archived)
                throw new WorkflowException("notPublished", "Only a published workflow's permissions can be changed here.");
            var published = JsonNode.Parse((await u.S.GetVersionAsync(id, rec.Version, ct))!.Json)!.AsObject();
            foreach (var (node, who) in changes) ApplyPermission(published, node, who);
            var hadDraft = rec.HasDraftChanges;
            var draft = JsonNode.Parse(rec.DraftJson)!.AsObject();
            // The draft gets the same changes where its nodes still exist (it may have removed some).
            foreach (var (node, who) in changes)
            {
                try { ApplyPermission(draft, node, who); }
                catch (WorkflowException) { }
            }
            rec.DraftJson = published.ToJsonString();
            await u.S.UpdateDefinitionAsync(rec, ct);
            var summary = await PublishInUnitAsync(u, id, note ?? "Permissions changed", actor, ct);
            if (hadDraft)
            {
                rec = await RequireDefinitionAsync(u, id, ct);
                draft["revision"] = rec.Version;
                rec.DraftJson = Normalize(draft.ToJsonString());
                var pub = await u.S.GetVersionAsync(id, rec.Version, ct);
                rec.HasDraftChanges = pub is null || Hash(rec.DraftJson) != pub.Hash;
                await u.S.UpdateDefinitionAsync(rec, ct);
                summary = await SummaryAsync(u, rec, ct);
            }
            await u.AuditAsync(new AuditEntry
            {
                Category = "definition", Action = "permissionsChanged", SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor,
                Data = new Dictionary<string, object?>
                {
                    ["version"] = (double)rec.Version,
                    ["changes"] = changes.Select(c => (object?)new Dictionary<string, object?> { ["node"] = c.Node, ["who"] = Values.FromJson(c.Who.ToJsonString()) }).ToList(),
                },
            }, ct);
            return summary;
        }, ct);
        await RefreshOwnershipAsync(ct);
        return result;
    }

    /// <summary>Sets who may do one node of a definition (see <see cref="UpdatePermissionsAsync"/>).</summary>
    private static void ApplyPermission(JsonObject def, string node, JsonArray who)
    {
        JsonNode Copy() => who.DeepClone();
        if (node == "trigger")
        {
            if (def["trigger"] is JsonObject trig && trig["type"]?.GetValue<string>() == TriggerTypes.Manual) { trig["who"] = Copy(); return; }
            throw new WorkflowException("unknownNode", "This workflow has no manual start whose people could change.");
        }
        if (node == "reset")
        {
            if (def["lifecycle"]?["reset"] is JsonObject reset) { reset["who"] = Copy(); return; }
            throw new WorkflowException("unknownNode", "This workflow cannot be reset.");
        }
        if (def["lifecycle"]?["transitions"] is JsonArray transitions)
            foreach (var t in transitions)
                if (t is JsonObject to && to["id"]?.GetValue<string>() == node) { to["who"] = Copy(); return; }
        if (FindStep(def, node) is { } step)
        {
            var type = step["type"]?.GetValue<string>();
            if (type is not ("approval" or "task")) throw new WorkflowException("unknownNode", $"Step '{node}' is not an approval or a task.");
            step["assignees"] = Copy();
            return;
        }
        throw new WorkflowException("unknownNode", $"'{node}' is not a transition, the reset, the manual start or a step of this workflow.");
    }

    /// <summary>The step object with this id, anywhere in a definition's JSON.</summary>
    private static JsonObject? FindStep(JsonNode? node, string id)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["type"] is JsonValue && o["id"]?.GetValue<string>() == id) return o;
                foreach (var (_, v) in o) if (FindStep(v, id) is { } found) return found;
                return null;
            case JsonArray a:
                foreach (var v in a) if (FindStep(v, id) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    /// <summary>The trigger index rows of a definition version: one per lifecycle (its owned field) or automation trigger.</summary>
    internal static List<TriggerRecord> TriggerRows(WorkflowDefinition def, string definitionId, string definitionKey, int version, DateTimeOffset publishedAt)
    {
        if (def.IsLifecycle && def.Lifecycle is { } lc)
        {
            return
            [
                new TriggerRecord
                {
                    Id = NewId(), DefinitionId = definitionId, DefinitionKey = definitionKey, Version = version, Type = "lifecycle", Entity = def.Entity, Field = lc.Field,
                    PublishedAt = publishedAt,
                    Data = Values.Serialize(new Dictionary<string, object?>
                    {
                        ["mode"] = lc.Mode ?? "enforce", ["initial"] = lc.States.FirstOrDefault(s => s.IsInitial)?.Key,
                        ["appliesWhen"] = lc.AppliesWhen is null ? null : Values.FromJson(JsonSerializer.Serialize(lc.AppliesWhen, Definitions.JsonOptions)),
                    }),
                },
            ];
        }
        if (def.Trigger is not { } trig) return [];
        return
        [
            new TriggerRecord
            {
                Id = NewId(), DefinitionId = definitionId, DefinitionKey = definitionKey, Version = version, Type = trig.Type, Entity = def.Entity, Field = trig.Field,
                PublishedAt = publishedAt, Lifecycle = trig.Lifecycle, Signal = trig.Signal,
                Data = trig.Cron is null ? null : Values.Serialize(new Dictionary<string, object?> { ["cron"] = trig.Cron }),
            },
        ];
    }

    /// <summary>Builds the trigger index rows for a published version (and timers for schedules and date triggers).</summary>
    private async Task IndexTriggersAsync(Uow u, DefinitionRecord rec, WorkflowDefinition def, int version, CancellationToken ct)
    {
        var trig = def.Trigger;
        // Changes are compared with the first publish, not this version's: events still in the outbox when a
        // new version is published belong to the workflow and must not be dropped (spec §5).
        // Restoring version 1 keeps its first publish time too (publish order, spec §6).
        var firstPublished = (await u.S.GetVersionAsync(rec.Id, 1, ct))?.PublishedAt ?? u.Now;
        await u.S.ReplaceTriggersAsync(rec.Id, TriggerRows(def, rec.Id, rec.Key, version, firstPublished), ct);

        // Re-plan schedule and date timers for the new version.
        foreach (var b in await u.S.FindDefinitionBookmarksAsync(rec.Id, BookmarkKinds.Schedule, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        if (trig?.Type == TriggerTypes.Schedule && trig.Cron is not null && Cron.TryParse(trig.Cron, out var cron, out _)) await PlanNextScheduleAsync(u, rec.Id, cron!, ct);
        if (trig?.Type == TriggerTypes.DateReached) await PlanDateTriggersForAllAsync(u, new CompiledDefinition(def, rec.Id, version), ct);
        else foreach (var b in await u.S.FindDefinitionBookmarksAsync(rec.Id, BookmarkKinds.DateReached, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
    }

    /// <summary>
    /// Archives a definition: its triggers, schedules and date timers are removed so it starts nothing new;
    /// running instances finish on their version. Audited as <c>definition.archived</c>.
    /// </summary>
    public Task<DefinitionSummary> ArchiveAsync(string id, WorkflowActor actor, CancellationToken ct = default) =>
        SetStatusAsync(id, DefinitionStatus.Archived, "archived", actor, ct);

    /// <summary>
    /// Restores an archived definition to published (or draft, if never published) and re-indexes its triggers.
    /// The published version is linted against the other published workflows first and lint errors refuse it
    /// (<c>lintErrors</c>), like publishing. Audited as <c>definition.restored</c>.
    /// </summary>
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
            if (status is null && rec.Version > 0 && rec.Status == DefinitionStatus.Archived)
            {
                // Coming back must not clash with what was published meanwhile (two fallback lifecycles for one
                // field, a key another workflow now uses…): the version is linted like a publish.
                var errors = (await LintAsync(u, (await u.S.GetVersionAsync(id, rec.Version, ct))!.Json, ct)).Where(p => p.Severity == "error").ToList();
                if (errors.Count > 0) throw new WorkflowException("lintErrors", $"Fix {errors.Count} problem(s) before restoring.", errors);
            }
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
            // The list shows the version's own name again, not the name the newer draft had.
            var old = Definitions.Parse(v.Json);
            rec.Title = old.Title;
            rec.Entity = old.Entity;
            rec.UpdatedAt = u.Now;
            rec.UpdatedBy = actor.Id;
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
