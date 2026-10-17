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
    /// <summary>The largest JSON body accepted by any endpoint (definitions, validation, test runs, transitions, signals…). Default 2 MB.</summary>
    public int MaxBodyBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>The largest form answers accepted with one task decision. Default 256 KB.</summary>
    public int MaxDecisionDataBytes { get; set; } = 256 * 1024;
    /// <summary>
    /// The most events one test run may replay. Default (and upper bound) <see cref="WorkflowEngine.MaxSimulationEvents"/>,
    /// which the engine enforces itself; set it lower to keep test runs cheaper.
    /// </summary>
    public int MaxSimulationEvents { get; set; } = WorkflowEngine.MaxSimulationEvents;
}

/// <summary>Reply of <c>GET /settings</c>.</summary>
/// <param name="Settings">The global settings (spec/schema/settings.schema.json).</param>
/// <param name="Revision">Their revision, to send back with <c>PUT /settings</c>.</param>
public sealed record SettingsResponse(JsonNode? Settings, int Revision);

/// <summary>Reply of <c>PUT /settings</c>.</summary>
/// <param name="Revision">The new revision.</param>
public sealed record RevisionResponse(int Revision);

/// <summary>Reply of <c>POST /signals/{key}</c>.</summary>
/// <param name="Resumed">How many waiting workflows the signal resumed.</param>
public sealed record SignalResponse(int Resumed);

/// <summary>Reply of <c>POST /audit/purge</c>.</summary>
/// <param name="Purged">How many entries were deleted.</param>
public sealed record PurgeResponse(int Purged);

/// <summary>A range of sequence numbers a purge deleted on purpose.</summary>
/// <param name="From">The first deleted sequence number.</param>
/// <param name="To">The last deleted sequence number.</param>
public sealed record SeqGap(long From, long To);

/// <summary>Reply of <c>POST /audit/verify</c>.</summary>
/// <param name="Valid">True when every entry checked out.</param>
/// <param name="Checked">How many entries were checked.</param>
/// <param name="BrokenAtSeq">The first entry that did not, if any.</param>
/// <param name="Problem">What was wrong with it.</param>
/// <param name="PurgedGaps">Gaps explained by purge checkpoints.</param>
public sealed record AuditVerifyResponse(bool Valid, long Checked, long? BrokenAtSeq, string? Problem, IReadOnlyList<SeqGap> PurgedGaps);

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
/// <param name="Comment">The person's remarks: required when the reset's <c>comment</c> is <c>required</c> (<c>commentRequired</c>), ignored when <c>none</c>; at most 2000 characters.</param>
public sealed record ResetBody(Dictionary<string, JsonNode?>? Input, string? Lifecycle, List<string>? Undo, string? Token = null, string? Comment = null);

/// <summary>One permission change: who may do a node of a workflow.</summary>
/// <param name="Node">A transition id, <c>reset</c>, <c>trigger</c> (a manual start) or an approval/task step id.</param>
/// <param name="Who">
/// The assignees (roles, groups, users, person fields of the record) who may do it. What empty means depends on
/// the node: a transition or the reset, nobody by hand; a manual start (<c>trigger</c>), anyone who can see the
/// record; an approval or task, refused (it needs someone, <c>lintErrors</c>).
/// </param>
public sealed record PermissionChange(string Node, JsonArray Who);

/// <summary>Body of <c>POST /definitions/{id}/permissions</c>.</summary>
/// <param name="Changes">The changes to apply.</param>
/// <param name="Note">What changed, shown in the version list (default "Permissions changed").</param>
public sealed record PermissionsBody(List<PermissionChange> Changes, string? Note = null);

/// <summary>The body of every error response: a stable code (spec), a message for people, and details (lint problems, form errors…).</summary>
/// <param name="Code">The error code, e.g. <c>notAllowed</c>, <c>lintErrors</c>, <c>wrongLifecycle</c>.</param>
/// <param name="Message">What went wrong, for people.</param>
/// <param name="Details">Extra data: lint problems for <c>lintErrors</c>, field errors for <c>invalidInput</c>.</param>
public sealed record WorkflowError(string Code, string Message, JsonNode? Details);

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

/// <summary>Body of manual-start requests.</summary>
/// <param name="Input">The form's answers.</param>
/// <param name="Lifecycle">Which lifecycle, when the entity has more than one.</param>
public sealed record InputBody(Dictionary<string, JsonNode?>? Input, string? Lifecycle);

/// <summary>Body of <c>POST /records/{entity}/{id}/transitions/{key}</c>.</summary>
/// <param name="Input">The transition form's answers.</param>
/// <param name="Lifecycle">Which lifecycle, when the entity has more than one.</param>
/// <param name="Comment">The person's remarks: required when the transition's <c>comment</c> is <c>required</c> (<c>commentRequired</c>), ignored when <c>none</c>; at most 2000 characters.</param>
public sealed record TransitionBody(Dictionary<string, JsonNode?>? Input, string? Lifecycle, string? Comment = null);

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
            Linter.StepLabels.Select(kv => new StepDescriptor(kv.Key, kv.Value, Linter.StepOutputs.GetValueOrDefault(kv.Key, []))).ToList())))
            .Produces<SchemaResponse>(), o.ViewPolicy);
        // Settings name webhook connections and secrets: editors only.
        Secure(g.MapGet("/settings", async (WorkflowEngine e, CancellationToken ct) =>
        {
            var (s, rev) = await e.GetSettingsAsync(ct);
            return Results.Ok(new SettingsResponse(JsonNode.Parse(s.ToJson()), rev));
        }).Produces<SettingsResponse>(), o.EditPolicy);
        Secure(g.MapPut("/settings", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<SettingsBody>(http, o.MaxBodyBytes, ct);
            return Results.Ok(new RevisionResponse(await e.SaveSettingsAsync(GlobalSettings.Parse(body.Settings?.ToJsonString()), body.Revision, Actor(http), ct)));
        }).Accepts<SettingsBody>(Json).Produces<RevisionResponse>().Produces<WorkflowError>(StatusCodes.Status400BadRequest)
            .Produces<WorkflowError>(StatusCodes.Status409Conflict), o.SettingsPolicy);
        Secure(g.MapGet("/triggers", (WorkflowEngine e, CancellationToken ct) => e.ListTriggersAsync(ct)), o.ViewPolicy);
        Secure(g.MapGet("/directory/users", async (string? q, int? take, IWorkflowIdentity identity, CancellationToken ct) =>
            Results.Ok(await identity.SearchUsersAsync(q, Math.Clamp(take ?? 20, 1, 100), ct))).Produces<IReadOnlyList<WorkflowUser>>(), o.ViewPolicy);
        Secure(g.MapGet("/directory/users/{id}", async (string id, IWorkflowIdentity identity, CancellationToken ct) =>
            await identity.GetUserAsync(id, ct) is { } u ? Results.Ok(u) : Results.NotFound()).Produces<WorkflowUser>().Produces(StatusCodes.Status404NotFound), o.ViewPolicy);
        // Who is asking and what they may do, so a UI can hide or lock what the server would refuse.
        Secure(g.MapGet("/me", async (HttpContext http, IAuthorizationService auth) =>
        {
            var me = Actor(http);
            var can = new MePermissions(
                await AllowedAsync(http, auth, o.EditPolicy), await AllowedAsync(http, auth, o.PublishPolicy), await AllowedAsync(http, auth, o.SettingsPolicy),
                await AllowedAsync(http, auth, o.InterventionPolicy), await AllowedAsync(http, auth, o.AuditPolicy));
            return Results.Ok(new MeResponse(me.Type, me.Id, me.Name, me.Roles ?? [], can));
        }).Produces<MeResponse>(), o.ViewPolicy);
    }

    private static void MapDefinitions(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/definitions", (WorkflowEngine e, CancellationToken ct) => e.ListDefinitionsAsync(ct)).Produces<IReadOnlyList<DefinitionSummary>>(), o.ViewPolicy);
        Secure(g.MapPost("/definitions", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            Results.Ok(await e.CreateDefinitionAsync(await ReadJsonTextAsync(http, o.MaxBodyBytes, ct), Actor(http), ct)))
            .Accepts<JsonObject>(Json).WithDescription(DefinitionBodyDescription).Produces<DefinitionSummary>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.EditPolicy);
        Secure(g.MapGet("/definitions/{id}", (string id, WorkflowEngine e, CancellationToken ct) => e.GetDefinitionAsync(id, ct)).Produces<DefinitionDetail>(), o.ViewPolicy);
        Secure(g.MapPut("/definitions/{id}", async (string id, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            Results.Ok(await e.SaveDraftAsync(id, await ReadJsonTextAsync(http, o.MaxBodyBytes, ct), Actor(http), ct)))
            .Accepts<JsonObject>(Json).WithDescription(DefinitionBodyDescription).Produces<DefinitionSummary>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.EditPolicy);
        Secure(g.MapDelete("/definitions/{id}", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
        {
            await e.DeleteDefinitionAsync(id, Actor(http), ct);
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent).Produces<WorkflowError>(StatusCodes.Status409Conflict), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/publish", async (string id, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<PublishBody>(http, o.MaxBodyBytes, ct);
            return Results.Ok(await e.PublishAsync(id, body?.Note, Actor(http), ct));
        }).Accepts<PublishBody>(Json).Produces<DefinitionSummary>().Produces<WorkflowError>(StatusCodes.Status400BadRequest), o.PublishPolicy);
        // Changing who may do steps publishes straight away (only those changes; drafts keep their edits).
        Secure(g.MapPost("/definitions/{id}/permissions", async (string id, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            {
                var body = await ReadBodyAsync<PermissionsBody>(http, o.MaxBodyBytes, ct);
                return Results.Ok(await e.UpdatePermissionsAsync(id, (body.Changes ?? []).Select(c => (c.Node, c.Who ?? [])).ToList(), body.Note, Actor(http), ct));
            })
            .Accepts<PermissionsBody>(Json)
            .Produces<PermissionsSummary>()
            .Produces<WorkflowError>(StatusCodes.Status400BadRequest)
            .Produces<WorkflowError>(StatusCodes.Status409Conflict), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/archive", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ArchiveAsync(id, Actor(http), ct))).Produces<DefinitionSummary>(), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/restore", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RestoreAsync(id, Actor(http), ct))).Produces<DefinitionSummary>(), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/rollback", async (string id, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<RollbackBody>(http, o.MaxBodyBytes, ct);
            return Results.Ok(await e.RollbackAsync(id, body.Version, Actor(http), ct));
        }).Accepts<RollbackBody>(Json).Produces<DefinitionSummary>(), o.PublishPolicy);
        Secure(g.MapGet("/definitions/{id}/versions/{version:int}", (string id, int version, WorkflowEngine e, CancellationToken ct) => e.GetVersionAsync(id, version, ct)), o.ViewPolicy);
        Secure(g.MapGet("/definitions/{id}/diff", (string id, WorkflowEngine e, CancellationToken ct) => e.DiffDraftAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapPost("/validate", async (HttpContext http, WorkflowEngine e, CancellationToken ct) =>
            Results.Ok(await e.ValidateAsync(await ReadJsonTextAsync(http, o.MaxBodyBytes, ct), ct)))
            .Accepts<JsonObject>(Json).WithDescription(DefinitionBodyDescription).Produces<IReadOnlyList<LintProblem>>(), o.EditPolicy);
        Secure(g.MapPost("/simulate", async (HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<SimulateBody>(http, o.MaxBodyBytes, ct);
            var request = body.Request ?? new SimulationRequest();
            var limit = Math.Min(o.MaxSimulationEvents, WorkflowEngine.MaxSimulationEvents);
            if (request.Events?.Count > limit)
                throw new WorkflowException("tooManyEvents", $"A test run can replay at most {limit} events.");
            // A test run reads the real record it starts from: the caller must be allowed to see it.
            if (request.EntityId is not null && (body.Definition as JsonObject)?["entity"] is JsonValue ev && ev.TryGetValue<string>(out var entity))
                await RequireRecordAsync(entity, request.EntityId, access, http, ct);
            return Results.Ok(await e.SimulateAsync(body.Definition?.ToJsonString() ?? "null", request, Actor(http), ct));
        }).Accepts<SimulateBody>(Json).Produces<SimulationResult>().Produces<WorkflowError>(StatusCodes.Status400BadRequest), o.EditPolicy);
    }

    private static void MapInstances(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/instances", async (string? definitionId, string? definitionKey, string? entity, string? entityId, string? status, string? kind, string? parentId,
            int? skip, int? take, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            var me = Actor(http);
            var query = new InstanceQuery
            {
                DefinitionId = definitionId, DefinitionKey = definitionKey, Entity = entity, EntityId = entityId, ParentId = parentId,
                Statuses = Split(status), Kinds = Split(kind) ?? [InstanceKinds.Automation, InstanceKinds.Record],
            };
            // Instances the caller may not see are left out of the items and of the total.
            return Results.Ok(await VisiblePageAsync(
                (s, t) => e.QueryInstancesAsync(new InstanceQuery
                {
                    DefinitionId = query.DefinitionId, DefinitionKey = query.DefinitionKey, Entity = query.Entity, EntityId = query.EntityId,
                    ParentId = query.ParentId, Statuses = query.Statuses, Kinds = query.Kinds, Skip = s, Take = t,
                }, ct),
                i => access.CanViewInstanceAsync(me, i, ct), access, Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 500)));
        }).Produces<Page<InstanceView>>().WithDescription(VisibleTotalDescription), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}", async (string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
            Results.Ok(await VisibleInstanceAsync(id, e, access, http, ct))).Produces<InstanceView>(), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}/definition", async (string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await VisibleInstanceAsync(id, e, access, http, ct);
            return Results.Ok(await e.GetInstanceDefinitionAsync(id, ct));
        }).Produces<JsonObject>(), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}/audit", (string id, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryAuditAsync(new AuditQuery { InstanceId = id, FromSeq = 1, Take = Math.Clamp(take ?? 500, 1, 5000) }, ct)), o.AuditPolicy);
        // Interventions act on what the caller could see: the instance must pass the access policy too.
        Secure(g.MapPost("/instances/{id}/cancel", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<ReasonBody>(http, o.MaxBodyBytes, ct);
            await VisibleInstanceAsync(id, e, access, http, ct);
            return Results.Ok(await e.CancelAsync(id, body?.Reason ?? "", Actor(http), ct));
        }).Accepts<ReasonBody>(Json).Produces<InstanceView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/retry", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<RetryBody>(http, o.MaxBodyBytes, ct);
            await VisibleInstanceAsync(id, e, access, http, ct);
            return Results.Ok(await e.RetryAsync(id, Actor(http), body?.Target, ct));
        }).Accepts<RetryBody>(Json).Produces<InstanceView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/skip", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<SkipBody>(http, o.MaxBodyBytes, ct);
            await VisibleInstanceAsync(id, e, access, http, ct);
            return Results.Ok(await e.SkipAsync(id, Map(body?.Outputs), Actor(http), body?.Target, ct));
        }).Accepts<SkipBody>(Json).Produces<InstanceView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/migrate", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<MigrateBody>(http, o.MaxBodyBytes, ct);
            await VisibleInstanceAsync(id, e, access, http, ct);
            return Results.Ok(await e.MigrateAsync(id, body.Version, body.StateMap, Actor(http), ct));
        }).Accepts<MigrateBody>(Json).Produces<InstanceView>(), o.InterventionPolicy);
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
            var statuses = Split(status) ?? (all ? null : [Storage.TaskStatus.Open]);
            // Tasks the caller may not see are left out of the items and of the total.
            return Results.Ok(await VisiblePageAsync(
                (s, t) => e.QueryTasksAsync(new TaskQuery { AssigneeId = all ? null : me.Id, Entity = entity, EntityId = entityId, Statuses = statuses, Skip = s, Take = t }, me, ct),
                t => access.CanViewTaskAsync(me, t, ct), access, Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 500)));
        }).Produces<Page<TaskView>>().WithDescription(VisibleTotalDescription), o.ViewPolicy);
        Secure(g.MapGet("/tasks/{id}", async (string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
            Results.Ok(await VisibleTaskAsync(id, e, access, http, ct))).Produces<TaskView>(), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/decide", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<DecideBody>(http, o.MaxBodyBytes, ct);
            var data = Map(body.Data);
            if (Encoding.UTF8.GetByteCount(Values.Serialize(data)) > o.MaxDecisionDataBytes)
                throw new WorkflowException("tooLarge", $"The answers are too large (at most {o.MaxDecisionDataBytes / 1024} KB).");
            await VisibleTaskAsync(id, e, access, http, ct);
            return Results.Ok(await e.DecideAsync(id, Actor(http), body.Outcome, body.Comment, data, ct));
        }).Accepts<DecideBody>(Json).Produces<TaskView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/delegate", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<DelegateBody>(http, o.MaxBodyBytes, ct);
            await VisibleTaskAsync(id, e, access, http, ct);
            return Results.Ok(await e.DelegateAsync(id, Actor(http), body.To, body.Note, ct));
        }).Accepts<DelegateBody>(Json).Produces<TaskView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/reassign", async (string id, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<ReassignBody>(http, o.MaxBodyBytes, ct);
            await VisibleTaskAsync(id, e, access, http, ct);
            return Results.Ok(await e.ReassignAsync(id, Actor(http), body.Assignees ?? [], body.Reason, ct));
        }).Accepts<ReassignBody>(Json).Produces<TaskView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.InterventionPolicy);
    }

    private static void MapRecords(RouteGroupBuilder g, WorkflowApiOptions o)
    {
        Secure(g.MapGet("/records/{entity}/{id}", async (string entity, string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.GetRecordAsync(entity, id, Actor(http), ct));
        }).Produces<RecordView>(), o.ViewPolicy);
        Secure(g.MapGet("/records/{entity}/{id}/history", async (string entity, string id, int? take, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await RecordHistoryAsync(e, entity, id, Math.Clamp(take ?? 200, 1, 2000), ct));
        }).Produces<IReadOnlyList<AuditRecord>>(), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/transitions/{key}", async (string entity, string id, string key, HttpContext http, WorkflowEngine e,
            IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<TransitionBody>(http, o.MaxBodyBytes, ct);
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.ApplyTransitionAsync(entity, id, key, Map(body?.Input), Actor(http), body?.Lifecycle, ct, body?.Comment));
        }).Accepts<TransitionBody>(Json).Produces<RecordView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.ViewPolicy);
        // Starting a record's workflow over: the lifecycle's reset.who decides who may (spec §6 reset).
        // What a reset would take back, for the people it allows (the reset list).
        Secure(g.MapGet("/records/{entity}/{id}/reset", async (string entity, string id, string? lifecycle, WorkflowEngine e,
            IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct) =>
        {
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.GetResetPreviewAsync(entity, id, Actor(http), lifecycle, ct));
        }).Produces<ResetPreview>().WithDescription(ResetListDescription), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/reset", async (string entity, string id, HttpContext http, WorkflowEngine e,
            IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<ResetBody>(http, o.MaxBodyBytes, ct);
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.ResetAsync(entity, id, Map(body?.Input), Actor(http), body?.Lifecycle, ct, body?.Undo, body?.Token, body?.Comment));
        }).Accepts<ResetBody>(Json).Produces<RecordView>().Produces<WorkflowError>(StatusCodes.Status409Conflict).WithDescription(ResetDescription), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/force-state", async (string entity, string id, HttpContext http, WorkflowEngine e,
            IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<ForceStateBody>(http, o.MaxBodyBytes, ct);
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.ForceStateAsync(entity, id, body.State, body.Reason, body.SkipSteps, Actor(http), body.Lifecycle, ct));
        }).Accepts<ForceStateBody>(Json).Produces<RecordView>(), o.InterventionPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/manual/{workflow}", async (string entity, string id, string workflow, HttpContext http, WorkflowEngine e,
            IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<InputBody>(http, o.MaxBodyBytes, ct);
            await RequireRecordAsync(entity, id, access, http, ct);
            return Results.Ok(await e.StartManualAsync(workflow, entity, id, Map(body?.Input), Actor(http), ct));
        }).Accepts<InputBody>(Json).Produces<InstanceView>().Produces<WorkflowError>(StatusCodes.Status409Conflict), o.ViewPolicy);
        Secure(g.MapPost("/manual/{workflow}", async (string workflow, HttpContext http, WorkflowEngine e, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<InputBody>(http, o.MaxBodyBytes, ct);
            return Results.Ok(await e.StartManualAsync(workflow, null, null, Map(body?.Input), Actor(http), ct));
        }).Accepts<InputBody>(Json).Produces<InstanceView>(), o.ViewPolicy);
        var signal = g.MapPost("/signals/{key}", async (string key, HttpContext http, WorkflowEngine e, IWorkflowAccessPolicy access, CancellationToken ct) =>
        {
            var body = await ReadOptionalBodyAsync<SignalBody>(http, o.MaxBodyBytes, ct);
            if (body?.Entity is { } entity && body.EntityId is { } entityId) await RequireRecordAsync(entity, entityId, access, http, ct);
            return Results.Ok(new SignalResponse(await e.SignalAsync(key, body?.Entity, body?.EntityId, Map(body?.Data), Actor(http), ct)));
        }).Accepts<SignalBody>(Json).Produces<SignalResponse>();
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
        Secure(g.MapGet("/audit/export", ExportAuditAsync).Produces<IReadOnlyList<AuditRecord>>(StatusCodes.Status200OK, Json, "text/csv"), o.AuditExportPolicy ?? o.AuditPolicy);
        Secure(g.MapPost("/audit/verify", async (long? fromSeq, WorkflowEngine e, CancellationToken ct) =>
        {
            var r = await e.VerifyAuditAsync(fromSeq ?? 1, ct);
            return Results.Ok(new AuditVerifyResponse(r.Valid, r.Checked, r.BrokenAtSeq, r.Problem, r.PurgedGaps.Select(x => new SeqGap(x.From, x.To)).ToList()));
        }).Produces<AuditVerifyResponse>(), o.AuditPolicy);
        // Purging deletes evidence: it needs the settings policy, not just the right to read the log.
        Secure(g.MapPost("/audit/purge", async (WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(new PurgeResponse(await e.PurgeAuditAsync(Actor(http), ct)))).Produces<PurgeResponse>(), o.SettingsPolicy);
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

    private const string Json = "application/json";

    private const string DefinitionBodyDescription = "The body is a workflow definition (spec/schema/definition.schema.json).";

    private const string VisibleTotalDescription =
        "Items the caller may not see (IWorkflowAccessPolicy) are left out of `items` and of `total`, and `skip`/`take` count visible items. " +
        "With a restrictive policy the server checks at most the first 5000 matches; past that, `total` also counts the unchecked rest.";

    /// <summary>How many matches a filtered list checks against a restrictive access policy before estimating the rest.</summary>
    private const int AccessScanLimit = 5000;
    private const int AccessScanBatch = 250;

    /// <summary>
    /// One page of the items the caller may see, with a total that counts only visible items. With the allow-all
    /// policy the store's own paging is used; otherwise matches are read in batches and checked one by one.
    /// </summary>
    internal static async Task<Page<T>> VisiblePageAsync<T>(Func<int, int, Task<Page<T>>> query, Func<T, ValueTask<bool>> visible,
        IWorkflowAccessPolicy access, int skip, int take)
    {
        if (access is AllowAllWorkflowAccessPolicy) return await query(skip, take);
        var items = new List<T>();
        var seen = 0;
        for (var offset = 0; ;)
        {
            var batch = await query(offset, AccessScanBatch);
            foreach (var item in batch.Items)
            {
                if (!await visible(item)) continue;
                if (seen >= skip && items.Count < take) items.Add(item);
                seen++;
            }
            offset += batch.Items.Count;
            if (batch.Items.Count < AccessScanBatch || offset >= batch.Total) return new Page<T>(items, seen);
            if (offset >= AccessScanLimit) return new Page<T>(items, seen + (batch.Total - offset));
        }
    }

    private static RouteHandlerBuilder Secure(RouteHandlerBuilder r, string? policy) => policy is null ? r : r.RequireAuthorization(policy);

    private static async Task<bool> AllowedAsync(HttpContext http, IAuthorizationService auth, string? policy) =>
        policy is null || (await auth.AuthorizeAsync(http.User, policy)).Succeeded;

    private static IResult NotAllowed(string message) => Error("notAllowed", message, 403);

    /// <summary>An error in the standard shape (<see cref="WorkflowError"/>).</summary>
    private static IResult Error(string code, string message, int status, object? details = null) =>
        Results.Json(new { code, message, details }, Model.Definitions.JsonOptions, statusCode: status);

    private static async Task RequireRecordAsync(string entity, string id, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanViewRecordAsync(Actor(http), entity, id, ct)) throw new WorkflowException("notAllowed", "You cannot see this record.");
    }

    private static async Task<TaskView> VisibleTaskAsync(string id, WorkflowEngine e, IWorkflowAccessPolicy access, HttpContext http, CancellationToken ct)
    {
        var me = Actor(http);
        var task = await e.GetTaskAsync(id, me, ct);
        if (!await access.CanViewTaskAsync(me, task, ct)) throw new WorkflowException("notAllowed", "You cannot see this task.");
        return task;
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
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Error("tooLarge", "The request is too large.", 413);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        {
            return Error("invalidJson", $"The request body is not valid JSON for this endpoint: {e.Message}", 400);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            // Values the request carried that the engine could not use (e.g. a duration out of range).
            return Error("invalidInput", e.Message, 400);
        }
    }

    /// <summary>Reads the request body as JSON text, refusing bodies over <paramref name="limit"/> bytes before parsing them.</summary>
    private static async Task<string> ReadJsonTextAsync(HttpContext http, int limit, CancellationToken ct)
    {
        var text = await ReadTextAsync(http, limit, ct);
        // Validate it is one JSON value before the engine sees it.
        using (JsonDocument.Parse(text)) { }
        return text;
    }

    /// <summary>The raw body as text, refused once it passes <paramref name="limit"/> bytes (whatever Content-Length claimed).</summary>
    private static async Task<string> ReadTextAsync(HttpContext http, int limit, CancellationToken ct)
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
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>A required typed body: size-capped, and malformed or empty JSON is a 400 <c>invalidJson</c>.</summary>
    private static async Task<T> ReadBodyAsync<T>(HttpContext http, int limit, CancellationToken ct) where T : class =>
        await ReadOptionalBodyAsync<T>(http, limit, ct) ?? throw new WorkflowException("invalidJson", "The request needs a JSON body.");

    /// <summary>An optional typed body: an empty body (or <c>null</c>) gives null; otherwise as <see cref="ReadBodyAsync{T}"/>.</summary>
    private static async Task<T?> ReadOptionalBodyAsync<T>(HttpContext http, int limit, CancellationToken ct) where T : class
    {
        var text = await ReadTextAsync(http, limit, ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, WebJson);
    }

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
            "notInIncident" or "alreadyRunning" or "alreadyAssigned" or "staleSettings" or "taskClosed" or "alreadyDecided" or "duplicateId" or "duplicateKey" or "finished" or "published" or "keyLocked" or "kindLocked" or "nothingToReset" or "notPublished" or "resetChanged" or "wrongLifecycle" => 409,
            "tooLarge" => 413,
            // Refused input: settings that fail validation, a value that is not one of a field's options, and the
            // run-time checks of steps (a webhook header or method the engine will not send, sub-workflows nested past
            // maxChainDepth) when they surface from a request (a transition or start that runs the step at once).
            "invalidSettings" or "unknownValue" or "badHeader" or "badMethod" or "subWorkflowDepth" => 400,
            _ => 400,
        };
        return Error(e.Code, e.Message, status, e.Details);
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
