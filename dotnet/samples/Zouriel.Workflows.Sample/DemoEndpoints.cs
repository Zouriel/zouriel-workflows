using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.AspNetCore;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Sample;

/// <summary>Body of <c>POST /api/demo/bank</c>.</summary>
/// <param name="Down">Whether the bank refuses calls.</param>
public sealed record BankSwitch(bool Down);

/// <summary>Body of <c>POST /api/demo/applications</c>.</summary>
/// <param name="Title">The application's title.</param>
/// <param name="Amount">The amount asked for.</param>
/// <param name="ProgrammeId">The programme applied to.</param>
/// <param name="Priority">How urgent it is.</param>
/// <param name="Deadline">When a decision is needed.</param>
public sealed record NewApplication(string Title, decimal Amount, int? ProgrammeId, Priority? Priority, DateOnly? Deadline);

/// <summary>
/// The sample app's own endpoints: plain CRUD on applications, like any business app. Workflows react
/// to these writes through the EF interceptor; nothing here calls the engine directly.
/// </summary>
public static class DemoEndpoints
{
    /// <summary>Where a playground visitor gets a session id (it needs no session itself).</summary>
    public const string SessionPath = "/api/playground/session";

    /// <summary>The rate-limit policy for new playground sessions (resets count too: each one issues a new session).</summary>
    public const string SessionRateLimit = "playground-session";

    /// <summary>The authorization policy for recording payments.</summary>
    public const string PaymentsPolicy = "demo.payments";

    /// <summary>Maps the demo app's CRUD endpoints. Everything except the list of demo users needs a signed-in user.</summary>
    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/demo/users", () => DemoUsers.All.Select(u => new { u.User.Id, u.User.Name, u.User.Email, u.Title, roles = u.User.Roles ?? [] })).WithTags("Demo app");
        var g = app.MapGroup("/api/demo").WithTags("Demo app").RequireAuthorization();

        g.MapGet("/programmes", async (SampleDb db) => await db.Programmes.OrderBy(p => p.Name).ToListAsync());

        g.MapGet("/applications", async (SampleDb db) => (await db.Applications.Include(a => a.Programme).Include(a => a.Documents).OrderByDescending(a => a.Id).ToListAsync()).Select(View));
        g.MapGet("/applications/{id:int}", async (int id, SampleDb db) =>
            await db.Applications.Include(a => a.Programme).Include(a => a.Documents).FirstOrDefaultAsync(a => a.Id == id) is { } a ? Results.Ok(View(a)) : Results.NotFound());

        g.MapPost("/applications", async (NewApplication body, SampleDb db, HttpContext http) =>
        {
            var me = WorkflowApi.Actor(http);
            if (!me.IsUser) return Results.Json(new { code = "notAllowed", message = "Choose who you are first." }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 120) return Results.Json(new { code = "invalidInput", message = "Give the application a title (up to 120 characters)." }, statusCode: 400);
            if (body.Amount is < 0 or > 1_000_000) return Results.Json(new { code = "invalidInput", message = "The amount must be between 0 and 1,000,000." }, statusCode: 400);
            if (await db.Applications.CountAsync() >= 300) return Results.Json(new { code = "limit", message = "This sandbox is full. Reset it to start again." }, statusCode: 400);
            var a = new Application
            {
                Title = body.Title.Trim(), Amount = body.Amount, ProgrammeId = body.ProgrammeId, Priority = body.Priority ?? Priority.Normal,
                Deadline = body.Deadline, ApplicantId = me.Id, ApplicantEmail = DemoUsers.Find(me.Id)?.Email,
                Documents = [new Document { Name = "Passport copy" }, new Document { Name = "Transcript" }],
            };
            db.Applications.Add(a);
            await db.SaveChangesAsync();
            return Results.Ok(View(a));
        });

        g.MapPatch("/applications/{id:int}", async (int id, JsonObject body, SampleDb db) =>
        {
            var a = await db.Applications.Include(x => x.Programme).Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            foreach (var (k, v) in body)
            {
                switch (k)
                {
                    case "title": a.Title = v?.GetValue<string>() ?? a.Title; break;
                    case "amount": a.Amount = v?.GetValue<decimal>() ?? 0; break;
                    case "priority": a.Priority = Enum.Parse<Priority>(v!.GetValue<string>(), true); break;
                    case "reviewer": a.ReviewerId = v?.GetValue<string>() is { Length: > 0 } r ? r : null; break;
                    case "deadline": a.Deadline = v?.GetValue<string>() is { Length: > 0 } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null; break;
                    case "notes": a.Notes = v?.GetValue<string>(); break;
                    case "paid": a.Paid = v?.GetValue<bool>() ?? false; break;
                    // Writing the status directly is refused while the lifecycle owns it (enforce mode).
                    case "status": a.Status = Enum.Parse<ApplicationStatus>(v!.GetValue<string>(), true); break;
                    default: return Results.Json(new { code = "invalidInput", message = $"'{k}' cannot be changed here." }, statusCode: 400);
                }
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch (WorkflowFieldOwnedException e)
            {
                return Results.Json(new { code = e.Code, message = e.Message }, statusCode: 409);
            }
            return Results.Ok(View(a));
        });

        g.MapDelete("/applications/{id:int}", async (int id, SampleDb db) =>
        {
            var a = await db.Applications.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            db.Documents.RemoveRange(a.Documents);
            db.Applications.Remove(a);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/applications/{id:int}/documents/{docId:int}/verify", async (int id, int docId, SampleDb db) =>
        {
            var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == docId && x.ApplicationId == id);
            if (d is null) return Results.NotFound();
            d.Verified = !d.Verified;
            await db.SaveChangesAsync();
            return Results.Ok(d);
        });

        // Raising payment.received resumes workflows waiting for the money, so only finance (and admins) may.
        g.MapPost("/applications/{id:int}/payment", async (int id, WorkflowEngine engine, HttpContext http) =>
            Results.Ok(new { resumed = await engine.SignalAsync("payment.received", "application", id.ToString(System.Globalization.CultureInfo.InvariantCulture), new() { ["amount"] = 1 }, WorkflowApi.Actor(http)) }))
            .RequireAuthorization(PaymentsPolicy);

        // The demo's bank: switch it off to see a payment workflow stop, and put it right from the panel.
        g.MapGet("/bank", (DemoBank bank) => new { down = bank.Down });
        g.MapPost("/bank", (BankSwitch body, DemoBank bank) => { bank.Down = body.Down; return new { down = bank.Down }; });

        g.MapGet("/inbox", (string? user, DemoInbox inbox) => inbox.For(user).Take(100).Select(n => new
        {
            n.Message.Channel, n.Message.Subject, n.Message.Body, n.Message.Link, n.Message.Entity, n.Message.EntityId,
            to = n.Message.To.Select(r => r.Name ?? r.Email), at = n.At,
        }));

        g.MapGet("/clock", (TimeProvider time) => new { now = time.GetUtcNow(), offset = (time as OffsetTimeProvider)?.Offset.ToString() });
    }

    private static object View(Application a) => new
    {
        a.Id, a.Title, status = a.Status?.ToString(), priority = a.Priority.ToString(), a.Amount, applicant = a.ApplicantId, applicantName = DemoUsers.Find(a.ApplicantId)?.Name,
        reviewer = a.ReviewerId, reviewerName = DemoUsers.Find(a.ReviewerId)?.Name, a.Deadline, a.Paid, a.Notes, a.ProgrammeId, programme = a.Programme?.Name,
        documents = a.Documents.Select(d => new { d.Id, d.Name, d.Verified }),
    };

    /// <summary>Playground-only endpoints: get a session, move the sandbox clock and start over.</summary>
    public static void MapPlaygroundEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(SessionPath, (SandboxManager boxes) => Results.Ok(new { session = boxes.IssueSessionId() }))
            .WithTags("Playground").RequireRateLimiting(SessionRateLimit);
        var g = app.MapGroup("/api/playground").WithTags("Playground");
        g.MapPost("/advance", async (AdvanceBody body, HttpContext http, SandboxManager boxes) =>
        {
            if (!IsoDuration.TryParse(body.Duration, out var d) || d.Negative || d.Approximate > TimeSpan.FromDays(60))
                return Results.Json(new { code = "invalidInput", message = "Move the clock forward by up to 60 days, e.g. PT1H or P2D." }, statusCode: 400);
            var box = (Sandbox)http.Items["sandbox"]!;
            box.Clock.Advance(d.Approximate);
            await boxes.RunBackgroundAsync(box, http.RequestAborted);
            return Results.Ok(new { now = box.Clock.GetUtcNow(), offset = box.Clock.Offset.ToString() });
        });
        g.MapPost("/settle", async (HttpContext http, SandboxManager boxes) =>
        {
            await boxes.RunBackgroundAsync((Sandbox)http.Items["sandbox"]!, http.RequestAborted);
            return Results.Ok();
        });
        // Starting over issues a new session (the old id stops working) and counts against the session rate limit,
        // so resets cannot be used to make sandboxes faster than new sessions allow.
        g.MapPost("/reset", (HttpContext http, SandboxManager boxes) =>
            Results.Ok(new { session = boxes.Reset(((Sandbox)http.Items["sandbox"]!).Id) }))
            .RequireRateLimiting(SessionRateLimit);
    }

    /// <summary>Body of <c>POST /api/playground/advance</c>.</summary>
    /// <param name="Duration">How far to move the clock (ISO 8601, up to 60 days).</param>
    public sealed record AdvanceBody(string Duration);
}
