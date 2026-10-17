using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.AspNetCore;
using Zouriel.Workflows.DependencyInjection;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.EntityFrameworkCore;

namespace Zouriel.Workflows.Sample;

/// <summary>An app action: workflows call it with "Run action → Create invoice".</summary>
/// <param name="db">The app's database (the same unit of work as the workflow).</param>
public sealed class CreateInvoice(SampleDb db) : IUndoableWorkflowAction
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        // The idempotency key makes retries safe: the same run asking twice gets the same invoice.
        var number = "INV-" + Math.Abs(context.IdempotencyKey.GetHashCode() % 100000).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
        if (!context.Simulation && context.EntityId is not null && int.TryParse(context.EntityId, out var id))
        {
            var app = await db.Applications.FindAsync([id], ct);
            if (app is not null)
            {
                app.Notes = $"{app.Notes} Invoice {number} for {Amount(context.Input)}.".Trim();
                // Saved here, while the workflow's write scope is active, so the change is the workflow's.
                await db.SaveChangesAsync(ct);
            }
        }
        return new Dictionary<string, object?> { ["invoiceNo"] = number };
    }

    /// <summary>A reset that takes the invoice back removes its note (a real app would void the invoice).</summary>
    public async Task UndoAsync(WorkflowActionUndoContext context, CancellationToken ct)
    {
        if (context.EntityId is null || !int.TryParse(context.EntityId, out var id) || context.Output.GetValueOrDefault("invoiceNo") is not string number) return;
        var app = await db.Applications.FindAsync([id], ct);
        if (app?.Notes is not { } notes) return;
        app.Notes = WithoutInvoice(notes, number);
        // Saved inside the reset's write scope, so the write starts no automations (spec §6).
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The notes without the sentence an invoice added, whatever the amount looks like ("for 1500.5."): up to the full
    /// stop that ends it. Running it twice (a retried reset) finds nothing the second time.
    /// </summary>
    public static string WithoutInvoice(string notes, string number) =>
        System.Text.RegularExpressions.Regex.Replace(notes, $@"\s*Invoice {System.Text.RegularExpressions.Regex.Escape(number)} for \S*?\.(?=\s|$)", "").Trim();

    /// <summary>The amount as the note shows it (invariant, so the undo can find it again).</summary>
    internal static string Amount(IReadOnlyDictionary<string, object?> input) =>
        Convert.ToString(input.GetValueOrDefault("amount"), System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>Registers and seeds the sample app.</summary>
public static class SampleSetup
{
    /// <summary>
    /// Demo sign-in and the sample's authorization policies. The root app and every playground sandbox
    /// register the same, because sandbox requests resolve these services from the sandbox.
    /// </summary>
    public static void AddSampleSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(DemoAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoAuthHandler>(DemoAuthHandler.SchemeName, null);
        services.AddAuthorization(o =>
        {
            o.AddPolicy("workflows.edit", p => p.RequireRole("admin", "head"));
            o.AddPolicy("workflows.intervene", p => p.RequireRole("admin", "head"));
            o.AddPolicy("workflows.audit", p => p.RequireRole("admin", "head"));
            o.AddPolicy("workflows.settings", p => p.RequireRole("admin"));
        });
    }

    /// <summary>
    /// Everything the sample needs, for one database. The playground calls this once per visitor sandbox,
    /// with <paramref name="webhooks"/> set to a client that never calls out.
    /// </summary>
    public static void AddSampleApp(this IServiceCollection services, string connectionString, TimeProvider time, bool runWorker, IWebhookClient? webhooks = null)
    {
        services.AddSingleton(time);
        services.AddSampleSecurity();
        if (webhooks is not null) services.AddSingleton(webhooks);
        services.AddDbContext<SampleDb>((sp, o) => o.UseSqlite(connectionString).AddWorkflowInterceptor(sp));
        services.AddWorkflows(wf => wf
            .UseEntityFrameworkCore<SampleDb>()
            .UseIdentity<DemoIdentity>()
            .UseNotifications<DemoInbox>()
            .Configure(s =>
            {
                s.Time = time;
                s.RunWorker = runWorker;
                s.PollInterval = TimeSpan.FromMilliseconds(500);
                s.RecordLinkTemplate = "/applications/{id}";
            })
            .Role("reviewer", "Reviewer").Role("finance", "Finance officer").Role("head", "Head of office").Role("admin", "Administrator")
            .Group("panel", "Review panel")
            .Channel("email", "Email").Channel("inApp", "In-app")
            .Entity<Programme>("programme", e => e
                .Label("Programme", "Programmes").Title(p => p.Name)
                .Field(p => p.Name)
                .Field(p => p.CoordinatorId, f => f.Key("coordinator").Label("Coordinator").User())
                .Field(p => p.Budget, f => f.Currency().Settable()))
            .Entity<Document>("document", e => e
                .Label("Document", "Documents").Title(d => d.Name)
                .Field(d => d.Name)
                .Field(d => d.Verified, f => f.Watchable().Settable()))
            .Entity<Application>("application", e => e
                .Label("Application", "Applications").Title(a => a.Title)
                .Field(a => a.Title, f => f.Watchable())
                .Field(a => a.Status, f => f.LifecycleCapable()
                    .OptionLabel("UnderReview", "Under review"))
                .Field(a => a.Priority, f => f.Watchable().Settable())
                .Field(a => a.Amount, f => f.Currency().Watchable().Settable())
                .Field(a => a.ApplicantId, f => f.Key("applicant").Label("Applicant").User())
                .Field(a => a.ApplicantEmail, f => f.Key("applicantEmail").Email().Sensitive())
                .Field(a => a.ReviewerId, f => f.Key("reviewer").Label("Reviewer").User().Watchable().Settable())
                .Field(a => a.Deadline, f => f.Watchable().Settable())
                .Field(a => a.Paid, f => f.Watchable().Settable())
                .Field(a => a.Notes, f => f.LongText().Settable())
                .Field(a => a.CreatedAt, f => f.Label("Created"))
                .Relation(a => a.Programme, "programme")
                .Collection(a => a.Documents, "documents"))
            .Action<CreateInvoice>("finance.createInvoice", a => a
                .Label("Create invoice").Category("Finance").For("application")
                .Input("amount", "Amount", "currency", required: true)
                .Input("memo", "Memo")
                .Output("invoiceNo", "Invoice number"))
            .Signal("payment.received", s => s.Label("Payment received").For("application").Field("amount", "Amount", "currency")));
        services.AddSingleton<DemoInbox>();
    }

    /// <summary>Creates the tables and fills them with demo records and published workflows.</summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleDb>();
            await db.Database.EnsureCreatedAsync(ct);
            if (await db.Programmes.AnyAsync(ct)) return;
            var eng = new Programme { Name = "Engineering", CoordinatorId = "omar", Budget = 120000 };
            var med = new Programme { Name = "Medicine", CoordinatorId = "rana", Budget = 200000 };
            db.Programmes.AddRange(eng, med);
            await db.SaveChangesAsync(ct);
        }
        var engine = services.GetRequiredService<WorkflowEngine>();
        var admin = WorkflowActor.User("hana", "Hana Yusuf", ["head", "admin"]);
        foreach (var name in new[] { "scholarship-lifecycle", "notify-reviewers", "finance-sign-off", "deadline-reminder" })
        {
            var json = ReadSeed(name);
            var def = Model.Definitions.Parse(json);
            await engine.CreateDefinitionAsync(json, admin, ct);
            await engine.PublishAsync(def.Id, "Sample workflow", admin, ct);
        }
        await engine.SaveSettingsAsync(Settings.GlobalSettings.Parse(ReadSeed("settings")), 0, admin, ct);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleDb>();
            var programmes = await db.Programmes.ToListAsync(ct);
            db.Applications.AddRange(
                new Application
                {
                    Title = "Engineering bursary – Ali", ApplicantId = "ali", ApplicantEmail = "ali@example.com", Amount = 4800, Programme = programmes[0],
                    Deadline = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
                    Documents = [new Document { Name = "Passport copy" }, new Document { Name = "Transcript" }],
                },
                new Application
                {
                    Title = "Medicine scholarship – Sara", ApplicantId = "sara", ApplicantEmail = "sara@example.com", Amount = 12500, Priority = Priority.High,
                    Programme = programmes[1], Deadline = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
                    Documents = [new Document { Name = "Passport copy", Verified = true }, new Document { Name = "Offer letter" }],
                });
            await db.SaveChangesAsync(ct);
        }
        // Let the change events (record creation) run before anyone looks.
        for (var i = 0; i < 10 && await engine.ProcessOutboxAsync(100, ct) + await engine.ProcessTimersAsync(100, ct) > 0; i++) { }
    }

    /// <summary>An embedded seed file (workflow definitions, settings).</summary>
    public static string ReadSeed(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream($"Zouriel.Workflows.Sample.Seed.{name}.json") ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>A SQLite database file in write-ahead-log mode (readers don't wait for the writer).</summary>
    public static string FileDatabase(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 30 }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        return cs;
    }
}
