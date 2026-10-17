using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
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
/// Authorization policy names per kind of operation. Leave a policy null to only require what the
/// route group requires. Define the policies with <c>AddAuthorization</c>.
/// </summary>
public sealed class WorkflowApiOptions
{
    /// <summary>Reading workflows, instances, records and one's own tasks.</summary>
    public string? ViewPolicy { get; set; }
    /// <summary>Creating and editing drafts, validating and test runs.</summary>
    public string? EditPolicy { get; set; }
    /// <summary>Publishing, archiving, restoring, rolling back and deleting workflows.</summary>
    public string? PublishPolicy { get; set; }
    /// <summary>Cancel, retry, skip, migrate, force state and reassign.</summary>
    public string? InterventionPolicy { get; set; }
    /// <summary>Reading, exporting, verifying and purging the audit log.</summary>
    public string? AuditPolicy { get; set; }
    /// <summary>Changing global settings.</summary>
    public string? SettingsPolicy { get; set; }
    /// <summary>Raising signals through the API (apps usually call <c>WorkflowEngine.SignalAsync</c> directly).</summary>
    public string? SignalPolicy { get; set; }
}

public sealed record PublishBody(string? Note);
public sealed record RollbackBody(int Version);
public sealed record SimulateBody(JsonNode Definition, SimulationRequest? Request);
public sealed record ReasonBody(string? Reason);
public sealed record SkipBody(Dictionary<string, JsonNode?>? Outputs);
public sealed record MigrateBody(int Version, Dictionary<string, string>? StateMap);
public sealed record DecideBody(string Outcome, string? Comment, Dictionary<string, JsonNode?>? Data);
public sealed record DelegateBody(string To, string? Note);
public sealed record ReassignBody(List<string> Assignees, string? Reason);
public sealed record InputBody(Dictionary<string, JsonNode?>? Input, string? Lifecycle);
public sealed record ForceStateBody(string State, string Reason, bool SkipSteps, string? Lifecycle);
public sealed record SignalBody(string? Entity, string? EntityId, Dictionary<string, JsonNode?>? Data);
public sealed record SettingsBody(JsonNode Settings, int Revision);
public sealed record StepDescriptor(string Type, string Label, IReadOnlyList<string> Outputs);
public sealed record SchemaResponse(int SpecVersion, WorkflowRegistry Registry, IReadOnlyList<StepDescriptor> Steps);

public static class WorkflowApi
{
    /// <summary>
    /// Maps the workflow REST API (spec <c>api/openapi.yaml</c>) under <paramref name="prefix"/>. Secure the
    /// returned group (e.g. <c>.RequireAuthorization()</c>) and set per-operation policies in <paramref name="configure"/>.
    /// </summary>
    public static RouteGroupBuilder MapWorkflowApi(this IEndpointRouteBuilder app, string prefix = "/api/workflows", Action<WorkflowApiOptions>? configure = null)
    {
        var o = new WorkflowApiOptions();
        configure?.Invoke(o);
        var g = app.MapGroup(prefix).WithTags("Workflows");
        g.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            catch (WorkflowException e)
            {
                return Problem(e);
            }
            catch (System.Text.Json.JsonException e)
            {
                return Results.Json(new { code = "invalidJson", message = e.Message }, statusCode: 400);
            }
        });

        RouteHandlerBuilder Secure(RouteHandlerBuilder r, string? policy) => policy is null ? r : r.RequireAuthorization(policy);

        // ── Schema, settings, directory ───────────────────────────────────
        Secure(g.MapGet("/schema", (WorkflowRegistry registry) => Results.Ok(new SchemaResponse(1, registry,
            Linter.StepLabels.Select(kv => new StepDescriptor(kv.Key, kv.Value, Linter.StepOutputs.GetValueOrDefault(kv.Key, []))).ToList()))), o.ViewPolicy);
        Secure(g.MapGet("/settings", async (WorkflowEngine e, CancellationToken ct) =>
        {
            var (s, rev) = await e.GetSettingsAsync(ct);
            return Results.Ok(new { settings = JsonNode.Parse(s.ToJson()), revision = rev });
        }), o.ViewPolicy);
        Secure(g.MapPut("/settings", async (SettingsBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
        {
            var rev = await e.SaveSettingsAsync(GlobalSettings.Parse(body.Settings.ToJsonString()), body.Revision, Actor(http), ct);
            return Results.Ok(new { revision = rev });
        }), o.SettingsPolicy);
        Secure(g.MapGet("/triggers", (WorkflowEngine e, CancellationToken ct) => e.ListTriggersAsync(ct)), o.ViewPolicy);
        Secure(g.MapGet("/directory/users", async (string? q, int? take, IWorkflowIdentity identity, CancellationToken ct) =>
            Results.Ok(await identity.SearchUsersAsync(q, Math.Clamp(take ?? 20, 1, 100), ct))), o.ViewPolicy);
        Secure(g.MapGet("/directory/users/{id}", async (string id, IWorkflowIdentity identity, CancellationToken ct) =>
            await identity.GetUserAsync(id, ct) is { } u ? Results.Ok(u) : Results.NotFound()), o.ViewPolicy);
        Secure(g.MapGet("/me", (HttpContext http) => Results.Ok(Actor(http))), o.ViewPolicy);

        // ── Definitions ───────────────────────────────────────────────────
        Secure(g.MapGet("/definitions", (WorkflowEngine e, CancellationToken ct) => e.ListDefinitionsAsync(ct)), o.ViewPolicy);
        Secure(g.MapPost("/definitions", async (JsonNode body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.CreateDefinitionAsync(body.ToJsonString(), Actor(http), ct))), o.EditPolicy);
        Secure(g.MapGet("/definitions/{id}", (string id, WorkflowEngine e, CancellationToken ct) => e.GetDefinitionAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapPut("/definitions/{id}", async (string id, JsonNode body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.SaveDraftAsync(id, body.ToJsonString(), Actor(http), ct))), o.EditPolicy);
        Secure(g.MapDelete("/definitions/{id}", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
        {
            await e.DeleteDefinitionAsync(id, Actor(http), ct);
            return Results.NoContent();
        }), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/publish", async (string id, PublishBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.PublishAsync(id, body?.Note, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/archive", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ArchiveAsync(id, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/restore", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RestoreAsync(id, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapPost("/definitions/{id}/rollback", async (string id, RollbackBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RollbackAsync(id, body.Version, Actor(http), ct))), o.PublishPolicy);
        Secure(g.MapGet("/definitions/{id}/versions/{version:int}", (string id, int version, WorkflowEngine e, CancellationToken ct) => e.GetVersionAsync(id, version, ct)), o.ViewPolicy);
        Secure(g.MapGet("/definitions/{id}/diff", (string id, WorkflowEngine e, CancellationToken ct) => e.DiffDraftAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapPost("/validate", async (JsonNode body, WorkflowEngine e, CancellationToken ct) => Results.Ok(await e.ValidateAsync(body.ToJsonString(), ct))), o.EditPolicy);
        Secure(g.MapPost("/simulate", async (SimulateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.SimulateAsync(body.Definition.ToJsonString(), body.Request ?? new SimulationRequest(), Actor(http), ct))), o.EditPolicy);

        // ── Instances ─────────────────────────────────────────────────────
        Secure(g.MapGet("/instances", (string? definitionId, string? definitionKey, string? entity, string? entityId, string? status, string? kind, string? parentId,
            int? skip, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryInstancesAsync(new InstanceQuery
            {
                DefinitionId = definitionId, DefinitionKey = definitionKey, Entity = entity, EntityId = entityId, ParentId = parentId,
                Statuses = Split(status), Kinds = Split(kind) ?? [InstanceKinds.Automation, InstanceKinds.Record], Skip = Math.Max(0, skip ?? 0), Take = Math.Clamp(take ?? 50, 1, 500),
            }, ct)), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}", (string id, WorkflowEngine e, CancellationToken ct) => e.GetInstanceAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}/definition", (string id, WorkflowEngine e, CancellationToken ct) => e.GetInstanceDefinitionAsync(id, ct)), o.ViewPolicy);
        Secure(g.MapGet("/instances/{id}/audit", (string id, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryAuditAsync(new AuditQuery { InstanceId = id, FromSeq = 1, Take = Math.Clamp(take ?? 500, 1, 5000) }, ct)), o.ViewPolicy);
        Secure(g.MapPost("/instances/{id}/cancel", async (string id, ReasonBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.CancelAsync(id, body?.Reason ?? "", Actor(http), ct))), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/retry", async (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.RetryAsync(id, Actor(http), ct))), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/skip", async (string id, SkipBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.SkipAsync(id, Map(body?.Outputs), Actor(http), ct))), o.InterventionPolicy);
        Secure(g.MapPost("/instances/{id}/migrate", async (string id, MigrateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.MigrateAsync(id, body.Version, body.StateMap, Actor(http), ct))), o.InterventionPolicy);

        // ── Tasks ─────────────────────────────────────────────────────────
        Secure(g.MapGet("/tasks", async (string? scope, string? status, string? entity, string? entityId, int? skip, int? take, WorkflowEngine e, IWorkflowIdentity identity,
            HttpContext http, CancellationToken ct) =>
        {
            var me = Actor(http);
            if (scope != "all" && me.Id is null) return Results.Ok(new Page<TaskView>([], 0));
            var q = new TaskQuery
            {
                AssigneeId = scope == "all" ? null : me.Id, Entity = entity, EntityId = entityId,
                Statuses = Split(status) ?? (scope == "all" ? null : [Storage.TaskStatus.Open]), Skip = Math.Max(0, skip ?? 0), Take = Math.Clamp(take ?? 50, 1, 500),
            };
            return Results.Ok(await e.QueryTasksAsync(q, me, ct));
        }), o.ViewPolicy);
        Secure(g.MapGet("/tasks/{id}", (string id, WorkflowEngine e, HttpContext http, CancellationToken ct) => e.GetTaskAsync(id, Actor(http), ct)), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/decide", async (string id, DecideBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.DecideAsync(id, Actor(http), body.Outcome, body.Comment, Map(body.Data), ct))), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/delegate", async (string id, DelegateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.DelegateAsync(id, Actor(http), body.To, body.Note, ct))), o.ViewPolicy);
        Secure(g.MapPost("/tasks/{id}/reassign", async (string id, ReassignBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ReassignAsync(id, Actor(http), body.Assignees, body.Reason, ct))), o.InterventionPolicy);

        // ── Records ───────────────────────────────────────────────────────
        Secure(g.MapGet("/records/{entity}/{id}", (string entity, string id, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            e.GetRecordAsync(entity, id, Actor(http), ct)), o.ViewPolicy);
        Secure(g.MapGet("/records/{entity}/{id}/history", (string entity, string id, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryAuditAsync(new AuditQuery { Entity = entity, EntityId = id, Take = Math.Clamp(take ?? 200, 1, 2000) }, ct)), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/transitions/{key}", async (string entity, string id, string key, InputBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ApplyTransitionAsync(entity, id, key, Map(body?.Input), Actor(http), body?.Lifecycle, ct))), o.ViewPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/force-state", async (string entity, string id, ForceStateBody body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.ForceStateAsync(entity, id, body.State, body.Reason, body.SkipSteps, Actor(http), body.Lifecycle, ct))), o.InterventionPolicy);
        Secure(g.MapPost("/records/{entity}/{id}/manual/{workflow}", async (string entity, string id, string workflow, InputBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.StartManualAsync(workflow, entity, id, Map(body?.Input), Actor(http), ct))), o.ViewPolicy);
        Secure(g.MapPost("/manual/{workflow}", async (string workflow, InputBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(await e.StartManualAsync(workflow, null, null, Map(body?.Input), Actor(http), ct))), o.ViewPolicy);
        Secure(g.MapPost("/signals/{key}", async (string key, SignalBody? body, WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(new { resumed = await e.SignalAsync(key, body?.Entity, body?.EntityId, Map(body?.Data), Actor(http), ct) })), o.SignalPolicy ?? o.InterventionPolicy);

        // ── Audit ─────────────────────────────────────────────────────────
        Secure(g.MapGet("/audit", (string? category, string? action, string? entity, string? entityId, string? instanceId, string? definitionId, string? actorId,
            DateTimeOffset? from, DateTimeOffset? to, long? before, int? take, WorkflowEngine e, CancellationToken ct) =>
            e.QueryAuditAsync(new AuditQuery
            {
                Category = category, Action = action, Entity = entity, EntityId = entityId, InstanceId = instanceId, DefinitionId = definitionId, ActorId = actorId,
                From = from, To = to, Before = before, Take = Math.Clamp(take ?? 100, 1, 1000),
            }, ct)), o.AuditPolicy);
        Secure(g.MapGet("/audit/export", async (string? format, string? category, string? entity, string? entityId, DateTimeOffset? from, DateTimeOffset? to,
            WorkflowEngine e, HttpContext http, CancellationToken ct) =>
        {
            var rows = await e.QueryAuditAsync(new AuditQuery { Category = category, Entity = entity, EntityId = entityId, From = from, To = to, FromSeq = 1, Take = 100_000 }, ct);
            await e.NoteAuditExportAsync(Actor(http), format ?? "json", rows.Count, new Dictionary<string, object?> { ["category"] = category, ["entity"] = entity, ["entityId"] = entityId }, ct);
            if (format == "csv") return Results.File(Encoding.UTF8.GetBytes(Csv(rows)), "text/csv", "workflow-audit.csv");
            return Results.File(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(rows, Model.Definitions.JsonOptions)), "application/json", "workflow-audit.json");
        }), o.AuditPolicy);
        Secure(g.MapPost("/audit/verify", async (long? fromSeq, WorkflowEngine e, CancellationToken ct) =>
        {
            var r = await e.VerifyAuditAsync(fromSeq ?? 1, ct);
            return Results.Ok(new { r.Valid, r.Checked, r.BrokenAtSeq, r.Problem, purgedGaps = r.PurgedGaps.Select(g => new { from = g.From, to = g.To }) });
        }), o.AuditPolicy);
        Secure(g.MapPost("/audit/purge", async (WorkflowEngine e, HttpContext http, CancellationToken ct) =>
            Results.Ok(new { purged = await e.PurgeAuditAsync(Actor(http), ct) })), o.AuditPolicy ?? o.SettingsPolicy);
        return g;
    }

    /// <summary>The caller as a workflow actor (anonymous callers are refused by the engine where it matters).</summary>
    public static WorkflowActor Actor(HttpContext http) =>
        http.RequestServices.GetRequiredService<IWorkflowActorAccessor>().Current ?? new WorkflowActor("anonymous", null, "Anonymous");

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
            "staleSettings" or "taskClosed" or "alreadyDecided" or "duplicateId" or "duplicateKey" or "finished" or "published" or "keyLocked" or "kindLocked" => 409,
            _ => 400,
        };
        return Results.Json(new { code = e.Code, message = e.Message, details = e.Details }, Model.Definitions.JsonOptions, statusCode: status);
    }

    private static string Csv(IEnumerable<AuditRecord> rows)
    {
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("seq,at,actorType,actorId,actorName,category,action,subjectType,subjectId,entity,entityId,instanceId,definitionId,before,after,data,correlationId,prevHash,hash\n");
        foreach (var r in rows)
        {
            sb.Append(r.Seq.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Q(Values.FormatInstant(r.At))).Append(',').Append(Q(r.ActorType)).Append(',')
              .Append(Q(r.ActorId)).Append(',').Append(Q(r.ActorName)).Append(',').Append(Q(r.Category)).Append(',').Append(Q(r.Action)).Append(',')
              .Append(Q(r.SubjectType)).Append(',').Append(Q(r.SubjectId)).Append(',').Append(Q(r.Entity)).Append(',').Append(Q(r.EntityId)).Append(',')
              .Append(Q(r.InstanceId)).Append(',').Append(Q(r.DefinitionId)).Append(',').Append(Q(r.Before)).Append(',').Append(Q(r.After)).Append(',')
              .Append(Q(r.Data)).Append(',').Append(Q(r.CorrelationId)).Append(',').Append(Q(r.PrevHash)).Append(',').Append(Q(r.Hash)).Append('\n');
        }
        return sb.ToString();
    }
}
