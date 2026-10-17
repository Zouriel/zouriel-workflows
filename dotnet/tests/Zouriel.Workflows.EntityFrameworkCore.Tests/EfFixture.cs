using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zouriel.Workflows.DependencyInjection;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Testing;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.EntityFrameworkCore.Tests;

/// <summary>One PostgreSQL container for the test run; each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString(string db) => new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Database = db }.ConnectionString;

    public Task InitializeAsync() => _pg.StartAsync();

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>An action with a scoped dependency (the app's DbContext), as real actions have.</summary>
public sealed class StampNotes(AppDb db) : IWorkflowAction
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var a = await db.Applications.FindAsync([int.Parse(context.EntityId!, System.Globalization.CultureInfo.InvariantCulture)], ct);
        var text = (string)context.Input["text"]!;
        a!.Notes = text;
        return new Dictionary<string, object?> { ["length"] = text.Length };
    }
}

/// <summary>An action that writes to the record (tracked and saved) and then fails: its writes must be undone.</summary>
public sealed class StampThenFail(AppDb db) : IWorkflowAction
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
    {
        var id = int.Parse(context.EntityId!, System.Globalization.CultureInfo.InvariantCulture);
        var a = await db.Applications.FindAsync([id], ct);
        a!.Notes = "half done";
        db.Add(new Programme { Name = "created by a failed action" });
        await db.SaveChangesAsync(ct);
        a.Amount = 99;
        throw new InvalidOperationException("The finance system is down.");
    }
}

/// <summary>A service provider wired like a real app: AppDb with the workflow tables and interceptor.</summary>
public sealed class TestApp : IAsyncDisposable
{
    public ServiceProvider Services { get; }
    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
    public FakeIdentity Identity { get; } = new();
    public RecordingNotifications Notifications { get; } = new();
    public WorkflowEngine Engine => Services.GetRequiredService<WorkflowEngine>();

    public TestApp(Action<DbContextOptionsBuilder> provider, bool sqlite = false, string? tenant = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowIdentity>(Identity);
        services.AddSingleton<INotificationSender>(Notifications);
        services.AddDbContext<AppDb>((sp, o) =>
        {
            provider(o);
            o.AddWorkflowInterceptor(sp);
        });
        services.AddScoped(sp => new AppDb(sp.GetRequiredService<DbContextOptions<AppDb>>(), sqlite));
        services.AddWorkflowsCore(wf => wf
            .UseEntityFrameworkCore<AppDb>()
            .Configure(s => { s.Time = Clock; s.Tenant = tenant; })
            .Entity<Programme>("programme", e => e.Title(p => p.Name).Field(p => p.Name, f => f.Settable()).Field(p => p.CoordinatorId, f => f.Key("coordinator").User()))
            .Entity<Application>("application", e => e
                .Label("Application").Title(a => a.Title)
                .Field(a => a.Title)
                .Field(a => a.Status, f => f.LifecycleCapable())
                .Field(a => a.Stage, f => f.Options(("new", "New"), ("checked", "Checked"), ("done", "Done")).Watchable().Settable())
                .Field(a => a.Amount, f => f.Currency().Watchable().Settable())
                .Field(a => a.Notes, f => f.Settable())
                .Field(a => a.ApplicantId, f => f.Key("applicant").User())
                .Field(a => a.Deadline, f => f.Watchable())
                .Field(a => a.Programme!.Name, f => f.Key("programmeName"))
                .Relation(a => a.Programme, "programme")
                .Collection(a => a.Documents, "documents"))
            .Entity<Document>("document", e => e.Field(d => d.Name).Field(d => d.Verified, f => f.Settable()))
            .Entity<Tag>("tag", e => e.Field(t => t.Name))
            .Entity<Receipt>("receipt", e => e.Field(r => r.Text, f => f.Settable()))
            .Entity<Ghost>("ghost", e => e.Field(g => g.Name, f => f.Settable()))
            .Action<StampThenFail>("stampThenFail", a => a.For("application"))
            .Action<StampNotes>("stamp", a => a.For("application").Input("text", "Text", required: true).Output("length", "Length", "number"))
            .Role("reviewer", "Reviewer"));
        Services = services.BuildServiceProvider();
        Identity.Add(new WorkflowUser("ali", "Ali"));
        Identity.Add(new WorkflowUser("rev1", "Rana", "rana@example.com", ["reviewer"]));
    }

    public async Task CreateDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<AppDb, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    public Task WithDbAsync(Func<AppDb, Task> work) => WithDbAsync<bool>(async db => { await work(db); return true; });

    public async Task SettleAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            var n = await Engine.ProcessOutboxAsync() + await Engine.ProcessTimersAsync();
            var failed = await WithDbAsync(db => db.Set<Storage.OutboxRecord>().Where(o => o.Error != null).Select(o => o.Error).FirstOrDefaultAsync());
            if (failed is not null) throw new InvalidOperationException($"Outbox failed: {failed}");
            if (n == 0) return;
        }
    }

    public async Task<string> PublishAsync(string json)
    {
        var admin = WorkflowActor.User("admin", "Admin");
        var def = Model.Definitions.Parse(json);
        await Engine.CreateDefinitionAsync(json, admin);
        try
        {
            await Engine.PublishAsync(def.Id, null, admin);
        }
        catch (WorkflowException e) when (e.Details is IEnumerable<Lint.LintProblem> problems)
        {
            throw new InvalidOperationException($"'{def.Key}' cannot be published: {string.Join(" ", problems.Select(p => $"[{p.Code}] {p.Message}"))}");
        }
        return def.Id;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
