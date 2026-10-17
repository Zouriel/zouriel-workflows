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
public record DefinitionSummary(
    string Id, string Key, string Kind, string Title, string? Entity, string Status, int Version, bool HasDraftChanges,
    DateTimeOffset UpdatedAt, string? UpdatedBy, int RunningInstances);

/// <summary>What <c>POST /definitions/{id}/permissions</c> did: the definition's summary plus the outcome of the change.</summary>
/// <param name="Changed">False when the changes left everything as it was (no version was published).</param>
/// <param name="DraftSkipped">The nodes the draft no longer has, so the draft did not get their change.</param>
/// <param name="RecordsMoved">Lifecycles: how many record instances moved from earlier versions to the new one.</param>
/// <param name="RecordsKept">
/// Lifecycles: how many record instances stayed on an older version because its states or transitions differ from
/// the new one (moving them could leave them in a state that version does not have); migrate them explicitly.
/// </param>
public sealed record PermissionsSummary(
    string Id, string Key, string Kind, string Title, string? Entity, string Status, int Version, bool HasDraftChanges,
    DateTimeOffset UpdatedAt, string? UpdatedBy, int RunningInstances, bool Changed, IReadOnlyList<string> DraftSkipped, int RecordsMoved, int RecordsKept = 0)
    : DefinitionSummary(Id, Key, Kind, Title, Entity, Status, Version, HasDraftChanges, UpdatedAt, UpdatedBy, RunningInstances)
{
    internal static PermissionsSummary Of(DefinitionSummary s, bool changed, IReadOnlyList<string> draftSkipped, int recordsMoved, int recordsKept = 0) =>
        new(s.Id, s.Key, s.Kind, s.Title, s.Entity, s.Status, s.Version, s.HasDraftChanges, s.UpdatedAt, s.UpdatedBy, s.RunningInstances, changed, draftSkipped, recordsMoved, recordsKept);
}

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
    /// too, where it still has the node; the others are returned in <see cref="PermissionsSummary.DraftSkipped"/>).
    /// A node is a transition id (its <c>who</c>), <c>reset</c> (the lifecycle reset's <c>who</c>),
    /// <c>trigger</c> (a manual trigger's <c>who</c>) or an approval/task step id (its <c>assignees</c>).
    /// What an empty <c>who</c> means depends on the node: nobody by hand for a transition or the reset,
    /// anyone who can see the record for a manual start; an approval or task needs someone (lint error).
    /// A change that alters nothing publishes nothing. A lifecycle's record instances move to the new
    /// version (states and transitions are unchanged, so they map 1:1), audited as <c>instance.migrated</c>
    /// with reason <c>permissions</c>; automation runs already in progress keep their version. Audited as
    /// <c>definition.permissionsChanged</c>.
    /// </summary>
    public async Task<PermissionsSummary> UpdatePermissionsAsync(string id, IReadOnlyList<(string Node, JsonArray Who)> changes, string? note, WorkflowActor actor,
        CancellationToken ct = default)
    {
        if (changes.Count == 0) throw new WorkflowException("invalidInput", "Say which permissions change.");
        var result = await RunAsync(async u =>
        {
            var rec = await RequireDefinitionAsync(u, id, ct);
            if (rec.Version == 0 || rec.Status == DefinitionStatus.Archived)
                throw new WorkflowException("notPublished", "Only a published workflow's permissions can be changed here.");
            var current = (await u.S.GetVersionAsync(id, rec.Version, ct))!;
            var published = JsonNode.Parse(current.Json)!.AsObject();
            foreach (var (node, who) in changes) ApplyPermission(published, node, who);
            // Nothing would change: no identical version is published.
            if (Hash(published.ToJsonString()) == current.Hash) return PermissionsSummary.Of(await SummaryAsync(u, rec, ct), false, [], 0);
            var hadDraft = rec.HasDraftChanges;
            var previousVersion = rec.Version;
            var draft = JsonNode.Parse(rec.DraftJson)!.AsObject();
            // The draft gets the same changes where its nodes still exist (it may have removed some).
            var skipped = new List<string>();
            foreach (var (node, who) in changes)
            {
                try { ApplyPermission(draft, node, who); }
                catch (WorkflowException) { skipped.Add(node); }
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
            // Existing records follow the new permissions (revoked people stop acting on them).
            var (moved, kept) = rec.Kind == WorkflowKinds.Lifecycle ? await MoveRecordsToVersionAsync(u, id, previousVersion, summary.Version, actor, "permissions", ct) : (0, 0);
            await u.AuditAsync(new AuditEntry
            {
                Category = "definition", Action = "permissionsChanged", SubjectType = "definition", SubjectId = id, DefinitionId = id, Actor = actor,
                Data = new Dictionary<string, object?>
                {
                    ["version"] = (double)summary.Version,
                    ["changes"] = changes.Select(c => (object?)new Dictionary<string, object?> { ["node"] = c.Node, ["who"] = Values.FromJson(c.Who.ToJsonString()) }).ToList(),
                    ["draftSkipped"] = skipped.Select(x => (object?)x).ToList(),
                    ["recordsMoved"] = (double)moved,
                    ["recordsKept"] = (double)kept,
                },
            }, ct);
            return PermissionsSummary.Of(summary, true, skipped, moved, kept);
        }, ct);
        await RefreshOwnershipAsync(ct);
        return result;
    }

    /// <summary>
    /// Moves a lifecycle's record instances (not cancelled) to <paramref name="version"/> without touching their state
    /// (a permissions change). Only instances whose version has the very same states and transitions as the new one
    /// move: those on <paramref name="previous"/> (the new version is it plus permission changes) and older versions
    /// that never changed the states or transitions. The others stay where they are (moving them could put them in a
    /// state the new version does not have) and are counted as kept. Each move is audited as <c>instance.migrated</c>.
    /// </summary>
    private async Task<(int Moved, int Kept)> MoveRecordsToVersionAsync(Uow u, string definitionId, int previous, int version, WorkflowActor actor, string reason, CancellationToken ct)
    {
        var target = await CompiledAsync(u, definitionId, version, ct);
        var sameShape = new Dictionary<int, bool> { [previous] = true };
        var moved = 0;
        var kept = 0;
        for (var skip = 0; ; skip += 500)
        {
            var page = await u.S.QueryInstancesAsync(new InstanceQuery
            {
                DefinitionId = definitionId, Kinds = [InstanceKinds.Record], Statuses = [InstanceStatus.Active, InstanceStatus.OutOfSync, InstanceStatus.Completed],
                Skip = skip, Take = 500,
            }, ct);
            foreach (var found in page)
            {
                var i = u.Records.TryGetValue(found.Id, out var held) ? held : found;
                if (i.Version >= version) continue;
                if (!sameShape.TryGetValue(i.Version, out var same))
                    sameShape[i.Version] = same = Shape(await CompiledAsync(u, definitionId, i.Version, ct)) == Shape(target);
                if (!same || (i.State is not null && target.StateById(i.State) is null))
                {
                    kept++;
                    continue;
                }
                var from = i.Version;
                i.Version = version;
                i.UpdatedAt = u.Now;
                await u.S.UpdateInstanceAsync(i, ct);
                await u.AuditAsync(new AuditEntry
                {
                    Category = "instance", Action = "migrated", SubjectType = "instance", SubjectId = i.Id, InstanceId = i.Id, DefinitionId = definitionId, Entity = i.Entity,
                    EntityId = i.EntityId, Actor = actor, CorrelationId = i.CorrelationId,
                    Before = new Dictionary<string, object?> { ["version"] = (double)from }, After = new Dictionary<string, object?> { ["version"] = (double)version },
                    Data = new Dictionary<string, object?> { ["reason"] = reason },
                }, ct);
                moved++;
            }
            if (page.Count < 500) return (moved, kept);
        }
    }

    /// <summary>A lifecycle's states and transitions (what record instances depend on), for comparing versions.</summary>
    private static string Shape(CompiledDefinition c) => JsonSerializer.Serialize(new
    {
        states = c.Lifecycle.States.Select(s => new { s.Id, s.Key, s.Category }),
        transitions = c.Lifecycle.Transitions.Select(t => new { t.Id, t.Key, t.To, from = t.From.Any ? null : t.From.States }),
    });

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
        if (AllSteps(def).FirstOrDefault(st => st["id"]?.GetValue<string>() == node) is { } step)
        {
            var type = step["type"]?.GetValue<string>();
            if (type is not ("approval" or "task")) throw new WorkflowException("unknownNode", $"Step '{node}' is not an approval or a task.");
            step["assignees"] = Copy();
            return;
        }
        throw new WorkflowException("unknownNode", $"'{node}' is not a transition, the reset, the manual start or a step of this workflow.");
    }

    /// <summary>
    /// Every step object of a definition's JSON, walking only the places steps live (spec §6, §7): the
    /// workflow's steps, a lifecycle's state, SLA, transition and reset steps, and the lists nested in steps.
    /// Forms, conditions and webhook bodies are never searched, so a form field with the same id is not a step.
    /// </summary>
    private static IEnumerable<JsonObject> AllSteps(JsonObject def)
    {
        static JsonObject? Obj(JsonNode? n) => n as JsonObject;
        static JsonArray? Arr(JsonNode? n) => n as JsonArray;
        static IEnumerable<JsonObject> In(JsonArray? list)
        {
            foreach (var s in (list ?? []).OfType<JsonObject>())
            {
                yield return s;
                foreach (var nested in Nested(s))
                foreach (var x in In(nested)) yield return x;
            }
        }
        static IEnumerable<JsonArray?> Nested(JsonObject s)
        {
            yield return Arr(s["steps"]);                              // forEach
            yield return Arr(Obj(s["onError"])?["steps"]);
            foreach (var p in Arr(s["paths"]) ?? []) yield return Arr(Obj(p)?["steps"]); // branch, parallel
            foreach (var c in Arr(s["cases"]) ?? []) yield return Arr(Obj(c)?["steps"]); // switch
            yield return Arr(Obj(s["otherwise"])?["steps"]);
            switch (s["outcomes"])
            {
                case JsonObject o:                                         // approval
                    yield return Arr(o["approved"]);
                    yield return Arr(o["rejected"]);
                    yield return Arr(o["timedOut"]);
                    break;
                case JsonArray a:                                          // task
                    foreach (var o in a) yield return Arr(Obj(o)?["steps"]);
                    break;
            }
            yield return Arr(Obj(s["onTimeout"])?["steps"]);              // task
            yield return Arr(Obj(Obj(s["until"])?["onTimeout"])?["steps"]); // wait
        }
        var roots = new List<JsonArray?> { Arr(def["steps"]) };
        if (Obj(def["lifecycle"]) is { } lc)
        {
            foreach (var st in Arr(lc["states"]) ?? [])
            {
                roots.Add(Arr(Obj(st)?["onEnter"]));
                roots.Add(Arr(Obj(st)?["onExit"]));
                roots.Add(Arr(Obj(Obj(st)?["sla"])?["steps"]));
            }
            foreach (var t in Arr(lc["transitions"]) ?? []) roots.Add(Arr(Obj(t)?["steps"]));
            roots.Add(Arr(Obj(lc["reset"])?["steps"]));
        }
        return roots.SelectMany(In);
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
            var wasArchived = rec.Status == DefinitionStatus.Archived;
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
                // An archived lifecycle moves nothing: its records' automatic transitions and SLAs stop too.
                if (rec.Kind == WorkflowKinds.Lifecycle) await ArchiveRecordTimersAsync(u, id, restore: false, ct);
            }
            else if (rec.Version > 0)
            {
                var v = await u.S.GetVersionAsync(id, rec.Version, ct);
                await IndexTriggersAsync(u, rec, Definitions.Parse(v!.Json), rec.Version, ct);
                if (wasArchived && rec.Kind == WorkflowKinds.Lifecycle) await ArchiveRecordTimersAsync(u, id, restore: true, ct);
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
