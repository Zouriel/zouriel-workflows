using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Lint;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.AspNetCore;

/// <summary>
/// Authorization policy names per kind of operation, and request limits. The whole API requires a
/// signed-in user unless <see cref="AllowAnonymous"/> is set; a null policy adds nothing to that. Define
/// the policies with <c>AddAuthorization</c>.
/// </summary>
public sealed class WorkflowApiOptions
{
    /// <summary>Reading workflows, instances, records and one's own tasks.</summary>
    public string? ViewPolicy { get; set; }
    /// <summary>Creating and editing drafts, validating, test runs, and reading global settings.</summary>
    public string? EditPolicy { get; set; }
    /// <summary>Publishing, archiving, restoring, rolling back and deleting workflows.</summary>
    public string? PublishPolicy { get; set; }
    /// <summary>Cancel, retry, skip, migrate, force state, reassign, and listing everyone's tasks.</summary>
    public string? InterventionPolicy { get; set; }
    /// <summary>Reading and verifying the raw audit log.</summary>
    public string? AuditPolicy { get; set; }
    /// <summary>Exporting the audit log (falls back to <see cref="AuditPolicy"/>).</summary>
    public string? AuditExportPolicy { get; set; }
    /// <summary>Changing global settings and purging the audit log.</summary>
    public string? SettingsPolicy { get; set; }
    /// <summary>
    /// Raising signals through the API (apps usually call <c>WorkflowEngine.SignalAsync</c> directly). Falls
    /// back to <see cref="InterventionPolicy"/>; with neither set, the endpoint refuses everyone, because a
    /// signal resumes workflows with data the caller chooses.
    /// </summary>
    public string? SignalPolicy { get; set; }
    /// <summary>Serve the API to callers who are not signed in (only for demos behind other protection).</summary>
    public bool AllowAnonymous { get; set; }
    /// <summary>The largest JSON body accepted (definitions, validation, test runs). Default 2 MB.</summary>
    public int MaxBodyBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>The largest form answers accepted with one task decision. Default 256 KB.</summary>
    public int MaxDecisionDataBytes { get; set; } = 256 * 1024;
    /// <summary>The most events one test run may replay. Default 50.</summary>
    public int MaxSimulationEvents { get; set; } = 50;
}

/// <summary>Reply of <c>GET /me</c>: the signed-in actor and what the API's policies let them do.</summary>
/// <param name="Type"><c>user</c>, or <c>anonymous</c> when the API allows callers who are not signed in.</param>
/// <param name="Id">The user id.</param>
/// <param name="Name">Their display name.</param>
/// <param name="Roles">Their roles.</param>
/// <param name="Can">Which kinds of operation they may perform.</param>
public sealed record MeResponse(string Type, string? Id, string? Name, IReadOnlyList<string> Roles, MePermissions Can);

/// <summary>What the caller may do, one flag per policy of <see cref="WorkflowApiOptions"/> (true when no policy is set).</summary>
/// <param name="Edit">Create and edit drafts, validate, test runs, read settings.</param>
/// <param name="Publish">Publish, archive, restore, roll back and delete.</param>
/// <param name="Settings">Change global settings and purge the audit log.</param>
/// <param name="Intervene">Cancel, retry, skip, migrate, force state, reassign.</param>
/// <param name="Audit">Read and verify the audit log.</param>
public sealed record MePermissions(bool Edit, bool Publish, bool Settings, bool Intervene, bool Audit);

/// <summary>Body of <c>POST /definitions/{id}/publish</c>.</summary>
/// <param name="Note">What changed, shown in the version list.</param>
public sealed record PublishBody(string? Note);

/// <summary>Body of <c>POST /records/{entity}/{id}/reset</c>.</summary>
/// <param name="Input">The reset form's answers, if it has a form.</param>
/// <param name="Lifecycle">Which lifecycle, when the record has more than one.</param>
/// <param name="Undo">The reset list's item ids to take back (from <c>GET …/reset</c>); empty = none.</param>
/// <param name="Token">The reset list's token (from <c>GET …/reset</c>); when the list has changed since, the reset fails with <c>resetChanged</c>.</param>
public sealed record ResetBody(Dictionary<string, JsonNode?>? Input, string? Lifecycle, List<string>? Undo, string? Token = null);

/// <summary>One permission change: who may do a node of a workflow.</summary>
/// <param name="Node">A transition id, <c>reset</c>, <c>trigger</c> (a manual start) or an approval/task step id.</param>
/// <param name="Who">The assignees (roles, groups, users, fields) who may do it; empty = nobody.</param>
public sealed record PermissionChange(string Node, JsonArray Who);

/// <summary>Body of <c>POST /definitions/{id}/permissions</c>.</summary>
/// <param name="Changes">The changes to apply.</param>
/// <param name="Note">What changed, shown in the version list (default "Permissions changed").</param>
public sealed record PermissionsBody(List<PermissionChange> Changes, string? Note);

/// <summary>Body of <c>POST /definitions/{id}/rollback</c>.</summary>
/// <param name="Version">The version to publish again.</param>
public sealed record RollbackBody(int Version);

/// <summary>Body of <c>POST /simulate</c>.</summary>
/// <param name="Definition">The definition to run (usually the draft).</param>
/// <param name="Request">The record, trigger and events of the test run.</param>
public sealed record SimulateBody(JsonNode Definition, SimulationRequest? Request);

/// <summary>A body carrying only a reason (e.g. cancelling an instance).</summary>
/// <param name="Reason">Why, in plain words.</param>
public sealed record ReasonBody(string? Reason);

/// <summary>Body of <c>POST /instances/{id}/skip</c>.</summary>
/// <param name="Outputs">Outputs to give the skipped step.</param>
/// <param name="Target">Which incident, by token id or failed step id, when several paths stopped (default: the first).</param>
public sealed record SkipBody(Dictionary<string, JsonNode?>? Outputs, string? Target = null);

/// <summary>Body of <c>POST /instances/{id}/retry</c>.</summary>
/// <param name="Target">Which incident, by token id or failed step id, when several paths stopped (default: the first).</param>
public sealed record RetryBody(string? Target);

/// <summary>Body of <c>POST /instances/{id}/migrate</c>.</summary>
/// <param name="Version">The version to move to.</param>
/// <param name="StateMap">Old state key → new state key, where keys differ.</param>
public sealed record MigrateBody(int Version, Dictionary<string, string>? StateMap);

/// <summary>Body of <c>POST /tasks/{id}/decide</c>.</summary>
/// <param name="Outcome">The chosen outcome key.</param>
/// <param name="Comment">An optional (or required) comment.</param>
/// <param name="Data">The task form's answers.</param>
public sealed record DecideBody(string Outcome, string? Comment, Dictionary<string, JsonNode?>? Data);

/// <summary>Body of <c>POST /tasks/{id}/delegate</c>.</summary>
/// <param name="To">The user id to hand the task to.</param>
/// <param name="Note">A note for them.</param>
public sealed record DelegateBody(string To, string? Note);

/// <summary>Body of <c>POST /tasks/{id}/reassign</c>.</summary>
/// <param name="Assignees">The new assignees' user ids.</param>
/// <param name="Reason">Why.</param>
public sealed record ReassignBody(List<string> Assignees, string? Reason);

/// <summary>Body of transition and manual-start requests.</summary>
/// <param name="Input">The form's answers.</param>
/// <param name="Lifecycle">Which lifecycle, when the entity has more than one.</param>
public sealed record InputBody(Dictionary<string, JsonNode?>? Input, string? Lifecycle);

/// <summary>Body of <c>POST /records/{entity}/{id}/force-state</c>.</summary>
/// <param name="State">The state key to move to.</param>
/// <param name="Reason">Why (required, audited).</param>
/// <param name="SkipSteps">Skip the onExit/onEnter steps.</param>
/// <param name="Lifecycle">Which lifecycle, when the entity has more than one.</param>
public sealed record ForceStateBody(string State, string Reason, bool SkipSteps, string? Lifecycle);

/// <summary>Body of <c>POST /signals/{key}</c>.</summary>
/// <param name="Entity">The record's entity, if the signal is about one.</param>
/// <param name="EntityId">The record's id.</param>
/// <param name="Data">The signal's data.</param>
public sealed record SignalBody(string? Entity, string? EntityId, Dictionary<string, JsonNode?>? Data);

/// <summary>Body of <c>PUT /settings</c>.</summary>
/// <param name="Settings">The new global settings.</param>
/// <param name="Revision">The revision they were based on (optimistic concurrency).</param>
public sealed record SettingsBody(JsonNode Settings, int Revision);

/// <summary>A step type the builder can offer.</summary>
/// <param name="Type">The step type key.</param>
/// <param name="Label">Its label.</param>
/// <param name="Outputs">The outputs later steps can read.</param>
public sealed record StepDescriptor(string Type, string Label, IReadOnlyList<string> Outputs);

/// <summary>Response of <c>GET /schema</c>.</summary>
/// <param name="SpecVersion">The spec version the engine implements.</param>
/// <param name="Registry">What workflows may read, watch, write and call.</param>
/// <param name="Steps">The step types.</param>
public sealed record SchemaResponse(int SpecVersion, WorkflowRegistry Registry, IReadOnlyList<StepDescriptor> Steps);

/// <summary>Maps the workflow REST API.</summary>
public static class WorkflowApi
{
    private const int ExportPage = 1000;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private const int MaxExportRows = 1_000_000;

    /// <summary>
    /// Maps the workflow REST API (spec <c>api/openapi.json</c>) under <paramref name="prefix"/>. Every
    /// endpoint requires a signed-in user (unless <see cref="WorkflowApiOptions.AllowAnonymous"/>) plus the
    /// per-operation policies set in <paramref name="configure"/>; record, instance and task reads are
    /// also checked against <see cref="IWorkflowAccessPolicy"/>.
    /// </summary>
    public static RouteGroupBuilder MapWorkflowApi(this IEndpointRouteBuilder app, string prefix = "/api/workflows", Action<WorkflowApiOptions>? configure = null)
    {
        var o = new WorkflowApiOptions();
        configure?.Invoke(o);
        var g = app.MapGroup(prefix).WithTags("Workflows");
        if (!o.AllowAnonymous) g.RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        g.AddEndpointFilter(MapErrorsAsync);
        MapSchemaAndSettings(g, o);
        MapDefinitions(g, o);
        MapInstances(g, o);
        MapTasks(g, o);
        MapRecords(g, o);
        MapAudit(g, o);
        return g;
    }

    /// <summary>
    /// The registry as editors see it: fields workflows can neither read, watch, set nor own are left out
    /// (they could not be used in a definition anyway, and their names may be private).
    /// </summary>
    internal static WorkflowRegistry PublicRegistry(WorkflowRegistry r) => new()
    {
        SpecVersion = r.SpecVersion, Actions = r.Actions, Signals = r.Signals, Channels = r.Channels, Roles = r.Roles, Groups = r.Groups,
        Entities = r.Entities.Select(e => new EntityDescriptor
        {
            Key = e.Key, Label = e.Label, PluralLabel = e.PluralLabel, TitleField = e.TitleField, ClrType = e.ClrType, IdGetter = e.IdGetter,
            Fields = e.Fields.Where(f => f.IsReadable || f.IsWatchable || f.IsSettable || f.IsLifecycle).ToList(),
        }).ToList(),
    };

    /// <summary>The caller as a workflow actor: only the signed-in principal counts (anything else is anonymous, which the engine refuses where it matters).</summary>
    public static WorkflowActor Actor(HttpContext http) =>
        http.RequestServices.GetRequiredService<IWorkflowActorAccessor>().Current ?? new WorkflowActor("anonymous", null, "Anonymous");

    // ── Groups of endpoints ──────────────────────────────────────────────

    private static void MapSchemaAndSettings(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/schema", (WorkflowRegistry registry) => Results.Ok(new SchemaResponse(1, PublicRegistry(registry),
            Linter.StepLabels.Select(kv => new StepDescriptor(kv.Key, kv.Value, Linter.StepOutputs.GetValueOrDefault(kv.Key, []))).ToList()))), o.ViewPolicy);
        // Settings name webhook connections and secrets: editors only.
        Secure(g.MapGet("/settings", async (WorkflowEngine e, CancellationToken ct) =>
        {
            var (s, rev) = await e.GetSettingsAsync(ct);
            return Results.Ok(new { settings = JsonNode.Parse(s.ToJson()), revision = rev });
        }), o.EditPolicy);
        Secure(g.MapPut("/settings", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<SettingsBody>(http, o.MaxBodyBytes, ct);
            return Results.Ok(new { revision = await e.SaveSettingsAsync(GlobalSettings.Parse(body.Settings.ToJsonString()), body.Revision, Actor(http), ct) });
        }), o.SettingsPolicy);
        Secure(g.MapGet("/triggers", (WorkflowEngine e, CancellationToken ct) => e.ListTriggersAsync(ct)), o.ViewPolicy);
        Secure(g.MapGet("/directory/users", async (string? q, int? take, IWorkflowIdentity identity, CancellationToken ct) =>
            Results.Ok(await identity.SearchUsersAsync(q, Math.Clamp(take ?? 20, 1, 100), ct))), o.ViewPolicy);
        Secure(g.MapGet("/directory/users/{id}", async (string id, IWorkflowIdentity identity, CancellationToken ct) =>
            await identity.GetUserAsync(id, ct) is { } u ? Results.Ok(u) : Results.NotFound()), o.ViewPolicy);
        // Who is asking and what they may do, so a UI can hide or lock what the server would refuse.
        Secure(g.MapGet("/me", async (HttpContext http, IAuthorizationService auth) =>
        {
            var me = Actor(http);
            var can = new MePermissions(
                await AllowedAsync(http, auth, o.EditPolicy), await AllowedAsync(http, auth, o.PublishPolicy), await AllowedAsync(http, auth, o.SettingsPolicy),
                await AllowedAsync(http, auth, o.InterventionPolicy), await AllowedAsync(http, auth, o.AuditPolicy));
            return Results.Ok(new MeResponse(me.Type, me.Id, me.Name, me.Roles ?? [], can));
        }), o.ViewPolicy);
    }

    private static void MapDefinitions(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/definitions", (WorkflowEngine e, CancellationToken ct) => e.ListDefinitionsAsync(ct)), o.ViewPolicy);
        Secure(g.MapPost("/definitions", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            Results.Ok(await e.CreateDefinitionAsync(await ReadJsonTextAsync(http, o.MaxBodyBytes, ct), Actor(http), ct))), o.EditPolicy);
        Secure(g.MapGet("/definitions/{id}", (string id, WorkflowEngine e, CancellationToken ct) => e.GetDefinitionAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapPut("/definitions/{id}", async (string id, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            Results.Ok(await e.SaveDraftAsync(id, await ReadJsonTextAsync(http, o.MaxBodyBytes, ct), Actor(http), ct))), o.EditPolicy);
        Secure(g.MapDelete("/definitions/{id}", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
        {
            await e.DeleteDefinitionAsync(id, Actor(http), ct);
            return Results.NoContent();
        }), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/publish", async (string id, PublishBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.PublishAsync(id, body?.Note, Actor(http), ct))), o.PublishPolicy);
        // Changing who may do steps publishes straight away (only those changes; drafts keep their edits).
        Secure(g.MapPost("/definitions/{id}/permissions", async (string id, PermissionsBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.UpdatePermissionsAsync(id, (body.Changes ?? []).Select(c => (c.Node, c.Who ?? [])).ToList(), body.Note, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/archive", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ArchiveAsync(id, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/restore", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RestoreAsync(id, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/rollback", async (string id, RollbackBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RollbackAsync(id, body.Version, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapGet("/definitions/{id}/versions/{version:int}", (string id, int version, WorkflowEngine e, CancellationToken ct) => e.GetVersionAsync(id, version, ct)), o.ViewPolicy);
        Secure(g.MapGet("/definitions/{id}/diff", (string id, WorkflowEngine e, CancellationToken ct) => e.DiffDraftAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapPost("/validate", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            Results.Ok(await e.ValidateAsync(await ReadJsonTextAsync(http, o.MaxBodyBytes, ct), ct))), o.EditPolicy);
        Secure(g.MapPost("/simulate", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<SimulateBody>(http, o.MaxBodyBytes, ct);
            var request = body.Request ?? new SimulationRequest();
            if (request.Events?.Count > o.MaxSimulationEvents)
                throw new WorkflowException("tooManyEvents", $"A test run can replay at most {o.MaxSimulationEvents} events.");
            return Results.Ok(await e.SimulateAsync(body.Definition.ToJsonString(), request, Actor(http), ct));
        }), o.EditPolicy);
    }

    private static void MapInstances(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/instances", async (string? definitionId, string? definitionKey, string? entity, string? entityId, string? status, string? kind, string? parentId,
            int? skip, int? take, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            var page = await e.QueryInstancesAsync(new InstanceQuery
            {
                DefinitionId = definitionId, DefinitionKey = definitionKey, Entity = entity, EntityId = entityId, ParentId = parentId,
                Statuses = Split(status), Kinds = Split(kind) ?? [InstanceKinds.Automation, InstanceKinds.Record], Skip = Math.Max(0, skip ?? 0), Take = Math.Clamp(take ?? 50, 1, 500),
            }, ct);
            // Instances the caller may not see are left out of the page (the total still counts them).
            var me = Actor(http);
            var visible = new List<InstanceView>();
            foreach (var i in page.Items)
                if (await access.CanViewInstanceAsync(me, i, ct)) visible.Add(i);
            return Results.Ok(new Page<InstanceView>(visible, page.Total));
        }), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}", async (string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
            Results.Ok(await VisibleInstanceAsync(id, e, access, http, ct))), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}/definition", async (string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await VisibleInstanceAsync(id, e, access, http, ct);
            return Results.Ok(await e.GetInstanceDefinitionAsync(id, ct));
        }), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}/audit", (string id, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryAuditAsync(new AuditQuery { InstanceId = id, FromSeq = 1, Take = Math.Clamp(take ?? 500, 1, 5000) }, ct)), o.AuditPolicy);
        Secure(g.MapPost("/instances/{id}/cancel", async (string id, ReasonBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.CancelAsync(id, body?.Reason ?? "", Actor(http), ct))), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/retry", async (string id, RetryBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RetryAsync(id, Actor(http), body?.Target, ct))), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/skip", async (string id, SkipBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.SkipAsync(id, Map(body?.Outputs), Actor(http), body?.Target, ct))), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/migrate", async (string id, MigrateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.MigrateAsync(id, body.Version, body.StateMap, Actor(http), ct))), o.InterventionPolicy);
    }

    private static void MapTasks(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/tasks", async (string? scope, string? status, string? entity, string? entityId, int? skip, int? take, WorkflowEngine e,
            IWorkflowAccessPolicy access, IAuthorizationService auth, HttpContext http, CancellationToken ct) =>
        {
            var me = Actor(http);
            var all = scope == "all";
            if (all && !await AllowedAsync(http, auth, o.InterventionPolicy)) return Results.Forbid();
            if (!all && me.Id is null) return Results.Ok(new Page<TaskView>([], 0));
            var page = await e.QueryTasksAsync(new TaskQuery
            {
                AssigneeId = all ? null : me.Id, Entity = entity, EntityId = entityId,
                Statuses = Split(status) ?? (all ? null : [Storage.TaskStatus.Open]), Skip = Math.Max(0, skip ?? 0), Take = Math.Clamp(take ?? 50, 1, 500),
            }, me, ct);
            var visible = new List<TaskView>();
            foreach (var t in page.Items)
                if (await access.CanViewTaskAsync(me, t, ct)) visible.Add(t);
            return Results.Ok(new Page<TaskView>(visible, page.Total));
        }), o.ViewPolicy);
        Secure(g.MapGet("/tasks/{id}", async (string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            var me = Actor(http);
            var task = await e.GetTaskAsync(id, me, ct);
            return await access.CanViewTaskAsync(me, task, ct) ? Results.Ok(task) : NotAllowed("You cannot see this task.");
        }), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/decide", async (string id, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<DecideBody>(http, o.MaxBodyBytes, ct);
            var data = Map(body.Data);
            if (Encoding.UTF8.GetByteCount(Values.Serialize(data)) > o.MaxDecisionDataBytes)
                throw new WorkflowException("tooLarge", $"The answers are too large (at most {o.MaxDecisionDataBytes / 1024} KB).");
            return Results.Ok(await e.DecideAsync(id, Actor(http), body.Outcome, body.Comment, data, ct));
        }), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/delegate", async (string id, DelegateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.DelegateAsync(id, Actor(http), body.To, body.Note, ct))), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/reassign", async (string id, ReassignBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ReassignAsync(id, Actor(http), body.Assignees, body.Reason, ct))), o.InterventionPolicy);
    }

    private static void MapRecords(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/records/{entity}/{id}", async (string entity, string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.GetRecordAsync(entity, id, Actor(http), ct));
        }), o.ViewPolicy);
        Secure(g.MapGet("/records/{entity}/{id}/history", async (string entity, string id, int? take, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await RecordHistoryAsync(e, entity, id, Math.Clamp(take ?? 200, 1, 2000), ct));
        }), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/transitions/{key}", async (string entity, string id, string key, InputBody? body, WorkflowEngine e,
            IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.ApplyTransitionAsync(entity, id, key, Map(body?.Input), Actor(http), body?.Lifecycle, ct));
        }), o.ViewPolicy);
        // Starting a record's workflow over: the lifecycle's reset.who decides who may (spec §6 reset).
        // What a reset would take back, for the people it allows (the reset list).
        Secure(g.MapGet("/records/{entity}/{id}/reset", async (string entity, string id, string? lifecycle, WorkflowEngine e,
            IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.GetResetPreviewAsync(entity, id, Actor(http), lifecycle, ct));
        }).Produces<ResetPreview>().WithDescription(ResetListDescription), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/reset", async (string entity, string id, ResetBody? body, WorkflowEngine e,
            IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.ResetAsync(entity, id, Map(body?.Input), Actor(http), body?.Lifecycle, ct, body?.Undo, body?.Token));
        }).WithDescription(ResetDescription), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/force-state", async (string entity, string id, ForceStateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ForceStateAsync(entity, id, body.State, body.Reason, body.SkipSteps, Actor(http), body.Lifecycle, ct))), o.InterventionPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/manual/{workflow}", async (string entity, string id, string workflow, InputBody? body, WorkflowEngine e,
            IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.StartManualAsync(workflow, entity, id, Map(body?.Input), Actor(http), ct));
        }), o.ViewPolicy);
        Secure(g.MapPost("/manual/{workflow}", async (string workflow, InputBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.StartManualAsync(workflow, null, null, Map(body?.Input), Actor(http), ct))), o.ViewPolicy);
        var signal = g.MapPost("/signals/{key}", async (string key, SignalBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(new { resumed = await e.SignalAsync(key, body?.Entity, body?.EntityId, Map(body?.Data), Actor(http), ct) }));
        var signalPolicy = o.SignalPolicy ?? o.InterventionPolicy;
        if (signalPolicy is not null) signal.RequireAuthorization(signalPolicy);
        else signal.AddEndpointFilter((_, _) => ValueTask.FromResult<object?>(NotAllowed("Signals cannot be raised through the API here.")));
    }

    private static void MapAudit(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/audit", (string? category, string? action, string? entity, string? entityId, string? instanceId, string? definitionId, string? actorId,
            DateTimeOffset? from, DateTimeOffset? to, long? before, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryAuditAsync(new AuditQuery
            {
                Category = category, Action = action, Entity = entity, EntityId = entityId, InstanceId = instanceId, DefinitionId = definitionId, ActorId = actorId,
                From = from, To = to, Before = before, Take = Math.Clamp(take ?? 100, 1, 1000),
            }, ct)), o.AuditPolicy);
        Secure(g.MapGet("/audit/export", ExportAuditAsync), o.AuditExportPolicy ?? o.AuditPolicy);
        Secure(g.MapPost("/audit/verify", async (long? fromSeq, WorkflowEngine e, CancellationToken ct) =>
        {
            var r = await e.VerifyAuditAsync(fromSeq ?? 1, ct);
            return Results.Ok(new { r.Valid, r.Checked, r.BrokenAtSeq, r.Problem, purgedGaps = r.PurgedGaps.Select(x => new { from = x.From, to = x.To }) });
        }), o.AuditPolicy);
        // Purging deletes evidence: it needs the settings policy, not just the right to read the log.
        Secure(g.MapPost("/audit/purge", async (WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(new { purged = await e.PurgeAuditAsync(Actor(http), ct) })), o.SettingsPolicy);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private const string ResetListDescription =
        "What a reset would take back (spec §6): the reset option and the items, oldest first, with a `token`. Item kinds: `created`, `field`, " +
        "`action`, `message`, `webhook`, `move` (another record moved by applyTransition; never undoable). 403 `notAllowed` when the caller is not " +
        "among the lifecycle's reset `who` or there is nothing to reset.";

    private const string ResetDescription =
        "Starts the record's workflow over (spec §6). `undo` lists item ids to take back; `token` (from GET) makes the reset fail with 409 " +
        "`resetChanged` when the list has changed since it was opened, as does an id no longer listed. 400 `notUndoable` for an item that " +
        "cannot be taken back, `invalidInput` for the form. Writes that take things back start no automations.";

    private static RouteHandlerBuilder Secure(RouteHandlerBuilder r, string? policy) => policy is null ? r : r.RequireAuthorization(policy);

    private static async Task<bool> AllowedAsync(HttpContext http, IAuthorizationService auth, string? policy) =>
        policy is null || (await auth.AuthorizeAsync(http.User, policy)).Succeeded;

    private static IResult NotAllowed(string message) => Results.Json(new { code = "notAllowed", message }, statusCode: 403);

    private static async Task RequireRecordAsync(string entity, string id, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanViewRecordAsync(Actor(http), entity, id, ct)) throw new WorkflowException("notAllowed", "You cannot see this record.");
    }

    private static async Task<InstanceView> VisibleInstanceAsync(string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct)
    {
        var view = await e.GetInstanceAsync(id, ct);
        if (!await access.CanViewInstanceAsync(Actor(http), view, ct)) throw new WorkflowException("notAllowed", "You cannot see this instance.");
        return view;
    }

    /// <summary>What a record page shows as history: its lifecycle entries and task decisions, newest first.</summary>
    private static async Task<IReadOnlyList<AuditRecord>> RecordHistoryAsync(WorkflowEngine e, string entity, string id, int take, CancellationToken ct)
    {
        var lifecycle = await e.QueryAuditAsync(new AuditQuery { Entity = entity, EntityId = id, Category = "lifecycle", Take = take }, ct);
        var decisions = await e.QueryAuditAsync(new AuditQuery { Entity = entity, EntityId = id, Category = "task", Action = "decided", Take = take }, ct);
        return lifecycle.Concat(decisions).OrderByDescending(a => a.Seq).Take(take).ToList();
    }

    /// <summary>Streams the audit log page by page as CSV or JSON, then records the export.</summary>
    private static async Task ExportAuditAsync(string? format, string? category, string? entity, string? entityId, DateTimeOffset? from, DateTimeOffset? to,
        WorkflowEngine e, HttpContext http, CancellationToken ct)
    {
        var csv = format == "csv";
        var res = http.Response;
        res.ContentType = csv ? "text/csv" : "application/json";
        res.Headers.ContentDisposition = $"attachment; filename=\"workflow-audit.{(csv ? "csv" : "json")}\"";
        await using var writer = new StreamWriter(res.Body, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        await writer.WriteAsync(csv ? CsvHeader : "[");
        var count = 0;
        long next = 1;
        while (count < MaxExportRows)
        {
            var page = await e.QueryAuditAsync(new AuditQuery { Category = category, Entity = entity, EntityId = entityId, From = from, To = to, FromSeq = next, Take = ExportPage }, ct);
            foreach (var r in page)
            {
                if (csv) await writer.WriteAsync(CsvRow(r));
                else await writer.WriteAsync((count == 0 ? "" : ",") + JsonSerializer.Serialize(r, Model.Definitions.JsonOptions));
                count++;
            }
            await writer.FlushAsync(ct);
            if (page.Count < ExportPage) break;
            next = page[^1].Seq + 1;
        }
        if (!csv) await writer.WriteAsync("]");
        await writer.FlushAsync(ct);
        await e.NoteAuditExportAsync(Actor(http), csv ? "csv" : "json", count, new Dictionary<string, object?> { ["category"] = category, ["entity"] = entity, ["entityId"] = entityId }, ct);
    }

    /// <summary>Turns workflow errors and bad input into JSON problems with stable codes.</summary>
    private static async ValueTask<object?> MapErrorsAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (WorkflowException e)
        {
            return Problem(e);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        {
            return Results.Json(new { code = "invalidJson", message = e.Message }, statusCode: 400);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            // Values the request carried that the engine could not use (e.g. a duration out of range).
            return Results.Json(new { code = "invalidInput", message = e.Message }, statusCode: 400);
        }
    }

    /// <summary>Reads the request body as JSON text, refusing bodies over <paramref name="limit"/> bytes before parsing them.</summary>
    private static async Task<string> ReadJsonTextAsync(HttpContext http, int limit, CancellationToken ct)
    {
        if (http.Request.ContentLength > limit) throw TooLarge(limit);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw TooLarge(limit);
            buffer.Write(chunk, 0, read);
        }
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        // Validate it is one JSON value before the engine sees it.
        using (JsonDocument.Parse(text)) { }
        return text;
    }

    private static async Task<T> ReadBodyAsync<T>(HttpContext http, int limit, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await ReadJsonTextAsync(http, limit, ct), WebJson)
        ?? throw new WorkflowException("invalidJson", "The request body is empty.");

    private static WorkflowException TooLarge(int limit) => new("tooLarge", $"The request is too large (at most {limit / (1024 * 1024.0):0.#} MB).");

    private static IReadOnlyList<string>? Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, object?> Map(Dictionary<string, JsonNode?>? body) =>
        body?.ToDictionary(kv => kv.Key, kv => Values.FromJson(kv.Value)) ?? [];

    private static IResult Problem(WorkflowException e)
    {
        var status = e.Code switch
        {
            "notAllowed" => 403,
            "notFound" or "noRecord" or "definitionNotFound" => 404,
            "staleSettings" or "taskClosed" or "alreadyDecided" or "duplicateId" or "duplicateKey" or "finished" or "published" or "keyLocked" or "kindLocked" or "nothingToReset" or "notPublished" or "resetChanged" => 409,
            "tooLarge" => 413,
            _ => 400,
        };
        return Results.Json(new { code = e.Code, message = e.Message, details = e.Details }, Model.Definitions.JsonOptions, statusCode: status);
    }

    private const string CsvHeader = "seq,at,actorType,actorId,actorName,category,action,subjectType,subjectId,entity,entityId,instanceId,definitionId,before,after,data,correlationId,prevHash,hash\n";

    private static string CsvRow(AuditRecord r) => string.Join(',',
        r.Seq.ToString(CultureInfo.InvariantCulture), Csv(Values.FormatInstant(r.At)), Csv(r.ActorType), Csv(r.ActorId), Csv(r.ActorName), Csv(r.Category), Csv(r.Action),
        Csv(r.SubjectType), Csv(r.SubjectId), Csv(r.Entity), Csv(r.EntityId), Csv(r.InstanceId), Csv(r.DefinitionId), Csv(r.Before), Csv(r.After), Csv(r.Data),
        Csv(r.CorrelationId), Csv(r.PrevHash), Csv(r.Hash)) + "\n";

    /// <summary>
    /// One quoted CSV cell. Text a spreadsheet would run as a formula (starting with = + - @, tab or carriage
    /// return) gets a leading apostrophe, so a record title cannot execute when an admin opens the export.
    /// </summary>
    internal static string Csv(string? s)
    {
        if (s is null) return "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
