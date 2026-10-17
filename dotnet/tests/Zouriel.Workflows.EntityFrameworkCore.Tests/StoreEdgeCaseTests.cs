using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore.Tests;

/// <summary>Cancels the save's token on the next command, as a request abort would mid-save.</summary>
public sealed class CancelOnce(CancellationTokenSource cts) : DbCommandInterceptor
{
    public bool Armed { get; set; }

    private void Maybe(CancellationToken ct)
    {
        if (!Armed) return;
        Armed = false;
        cts.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
    {
        Maybe(ct);
        return base.ReaderExecutingAsync(c, e, r, ct);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default)
    {
        Maybe(ct);
        return base.NonQueryExecutingAsync(c, e, r, ct);
    }
}

/// <summary>Edge cases found by the audit: failed workflow writes, tenants sharing an audit table, escaped assignee ids, cancelled saves.</summary>
public abstract class StoreEdgeCaseTests : IAsyncLifetime
{
    protected TestApp App { get; private set; } = null!;

    /// <summary>An app on this test's database writing to the audit chain of <paramref name="tenant"/>.</summary>
    protected abstract TestApp CreateApp(string? tenant);

    /// <summary>Points a hand-made context at this test's database.</summary>
    protected abstract void UseDatabase(DbContextOptionsBuilder o);

    /// <summary>SQL making the database refuse <c>Notes = 'boom'</c> on applications, as a constraint or trigger of the app would.</summary>
    protected abstract string RefuseBoomSql { get; }

    protected abstract bool Sqlite { get; }

    public async Task InitializeAsync()
    {
        App = CreateApp("a");
        await App.CreateDatabaseAsync();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();

    private const string Def = """
    { "specVersion": 1, "id": "ID", "key": "KEY", "kind": "automation", "title": "T", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
      "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "VALUE" } } ] } ] }
    """;

    private Task<int> NewApplicationAsync() => App.WithDbAsync(async db =>
    {
        var x = new Application { Title = "G", ApplicantId = "ali", Stage = "new" };
        db.Add(x);
        await db.SaveChangesAsync();
        return x.Id;
    });

    [Fact]
    public async Task A_setFields_write_the_database_refuses_fails_into_an_incident_and_the_unit_still_commits()
    {
        await App.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseBoomSql));
        await App.PublishAsync(Def.Replace("ID", "d9").Replace("KEY", "k9").Replace("VALUE", "boom"));
        var id = await NewApplicationAsync();
        await App.WithDbAsync(async db => { (await db.Applications.FindAsync(id))!.Stage = "checked"; await db.SaveChangesAsync(); });
        await App.Engine.ProcessOutboxAsync();
        await App.Engine.ProcessOutboxAsync();
        // Before the fix the refused values stayed tracked, the commit wrote them again and the whole start failed (no instance at all).
        var instance = await App.WithDbAsync(db => db.Set<InstanceRecord>().AsNoTracking().SingleAsync());
        Assert.Equal(InstanceStatus.Incident, instance.Status);
        Assert.Contains("DbUpdateException", instance.Error, StringComparison.Ordinal);
        Assert.False(await App.WithDbAsync(db => db.Set<AuditRecord>().AnyAsync(a => a.Category == "trigger" && a.Action == "failed")));
        Assert.Null(await App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Notes).FirstAsync()));
    }

    [Fact]
    public async Task Tasks_are_found_for_assignee_ids_the_json_column_escapes()
    {
        var store = App.Engine.Options.Store;
        var ids = new[] { "ann+1@x.com", "o'brien", "zoë", "plain" };
        await using (var s = await store.BeginAsync())
        {
            foreach (var who in ids)
                await s.AddTaskAsync(new TaskRecord
                {
                    Id = Guid.NewGuid().ToString("N"), InstanceId = "i", NodeId = "n", TokenId = "t", Title = who,
                    Assignees = Json.Values.Serialize(new List<object?> { who }), DueAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                });
            await s.CommitAsync();
        }
        await using (var s = await store.BeginAsync())
        {
            foreach (var who in ids)
            {
                var mine = await s.QueryTasksAsync(new TaskQuery { AssigneeId = who });
                Assert.Equal(who, Assert.Single(mine).Title);
                Assert.Single(await s.QueryTasksAsync(new TaskQuery { AssigneeAnyOf = [who, "nobody"] }));
            }
        }
    }

    [Fact]
    public async Task Tenants_sharing_the_audit_table_read_purge_and_verify_only_their_own_chain()
    {
        await using var b = CreateApp("b");
        var admin = WorkflowActor.User("admin", "Admin");
        await App.Engine.CreateDefinitionAsync(Def.Replace("ID", "d1").Replace("KEY", "k1"), admin);
        await b.Engine.CreateDefinitionAsync(Def.Replace("ID", "d2").Replace("KEY", "k2"), admin);

        Assert.All(await App.Engine.QueryAuditAsync(new AuditQuery()), e => Assert.Equal("a", e.Tenant));
        Assert.All(await b.Engine.QueryAuditAsync(new AuditQuery()), e => Assert.Equal("b", e.Tenant));
        var check = await App.Engine.VerifyAuditAsync();
        Assert.True(check.Valid, check.Problem);

        // Tenant a purges its definition entries; tenant b's stay, and both chains still verify.
        var (s, rev) = await App.Engine.GetSettingsAsync();
        s.Audit = new AuditSettings { RetentionDays = new() { ["definition"] = 1 } };
        await App.Engine.SaveSettingsAsync(s, rev, admin);
        App.Clock.Advance(TimeSpan.FromDays(3));
        b.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, await App.Engine.PurgeAuditAsync(admin));
        Assert.Contains(await b.Engine.QueryAuditAsync(new AuditQuery { Category = "definition" }), e => e.DefinitionId == "d2");
        Assert.True((await App.Engine.VerifyAuditAsync()).Valid);
        var checkB = await b.Engine.VerifyAuditAsync();
        Assert.True(checkB.Valid, checkB.Problem);
    }

    private static InstanceRecord RecordInstance(string id, string status) => new()
    {
        Id = id, DefinitionId = "lc", DefinitionKey = "review", Version = 1, Kind = InstanceKinds.Record, Entity = "application", EntityId = "7",
        Status = status, StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task The_database_refuses_a_second_live_record_instance_of_one_workflow_for_one_record()
    {
        var store = App.Engine.Options.Store;
        await using (var s = await store.BeginAsync())
        {
            // Finished ones do not count: a record may have had any number of earlier instances.
            await s.AddInstanceAsync(RecordInstance("old1", InstanceStatus.Cancelled));
            await s.AddInstanceAsync(RecordInstance("old2", InstanceStatus.Completed));
            await s.AddInstanceAsync(RecordInstance("live", InstanceStatus.Active));
            await s.CommitAsync();
        }
        await using (var s = await store.BeginAsync())
        {
            await s.AddInstanceAsync(RecordInstance("racer", InstanceStatus.Active));
            // A race between units: the loser is retried by the engine.
            await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => s.CommitAsync());
        }
        await using (var s = await store.BeginAsync())
        {
            // Another record, or an automation run of the same record, is fine.
            var other = RecordInstance("other", InstanceStatus.Active);
            other.EntityId = "8";
            await s.AddInstanceAsync(other);
            var run = RecordInstance("run", InstanceStatus.Running);
            run.Kind = InstanceKinds.Automation;
            await s.AddInstanceAsync(run);
            await s.CommitAsync();
        }
    }

    private async Task<AppDb> CancellableDbAsync(CancelOnce cancel)
    {
        await Task.CompletedTask;
        var o = new DbContextOptionsBuilder<AppDb>();
        UseDatabase(o);
        o.AddInterceptors(App.Services.GetRequiredService<WorkflowSaveChangesInterceptor>(), cancel);
        return new AppDb(o.Options, Sqlite);
    }

    [Fact]
    public async Task A_cancelled_save_that_is_retried_writes_its_change_event_once()
    {
        var id = await NewApplicationAsync();
        using var cts = new CancellationTokenSource();
        var cancel = new CancelOnce(cts);
        await using (var db = await CancellableDbAsync(cancel))
        {
            (await db.Applications.FindAsync(id))!.Stage = "checked";
            cancel.Armed = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cts.Token));
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await App.WithDbAsync(db => db.Set<OutboxRecord>().CountAsync(o => o.Payload.Contains("\"updated\""))));
    }

    [Fact]
    public async Task A_cancelled_insert_that_is_retried_writes_its_created_event_once()
    {
        using var cts = new CancellationTokenSource();
        var cancel = new CancelOnce(cts);
        await using (var db = await CancellableDbAsync(cancel))
        {
            db.Add(new Application { Title = "N", ApplicantId = "ali", Stage = "new" });
            cancel.Armed = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cts.Token));
            await db.SaveChangesAsync();
        }
        var created = await App.WithDbAsync(db => db.Set<OutboxRecord>().CountAsync(o => o.Payload.Contains("\"created\"")));
        var apps = await App.WithDbAsync(db => db.Applications.CountAsync());
        Assert.Equal((1, 1), (apps, created));
    }
}

public sealed class SqliteStoreEdgeCaseTests : StoreEdgeCaseTests, IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"wf-edge-{Guid.NewGuid():N}.db");

    protected override TestApp CreateApp(string? tenant) => new(o => o.UseSqlite($"Data Source={_file}"), sqlite: true, tenant: tenant);
    protected override void UseDatabase(DbContextOptionsBuilder o) => o.UseSqlite($"Data Source={_file}");
    protected override bool Sqlite => true;
    protected override string RefuseBoomSql =>
        "CREATE TRIGGER no_boom BEFORE UPDATE ON Applications WHEN NEW.Notes = 'boom' BEGIN SELECT RAISE(ABORT, 'notes may not be boom'); END;";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _file, _file + "-wal", _file + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private async Task WalAsync() => await App.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;"));

    [Fact]
    public async Task A_unit_that_only_reads_does_not_block_the_apps_writes()
    {
        await WalAsync();
        await using var unit = await App.Engine.Options.Store.BeginAsync();
        _ = await unit.GetSettingsAsync(); // a read-only unit, like an API view
        _ = await unit.QueryInstancesAsync(new InstanceQuery());
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={_file};Default Timeout=1").Options;
        await using var db = new AppDb(options, sqlite: true);
        db.Add(new Programme { Name = "x" });
        // Before the fix the unit began IMMEDIATE and held the write lock: this failed with 'database is locked'.
        Assert.Equal(1, await db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_unit_whose_reads_went_stale_is_retried_instead_of_writing()
    {
        await WalAsync();
        await using var unit = await App.Engine.Options.Store.BeginAsync();
        _ = await unit.GetSettingsAsync();
        await App.WithDbAsync(async db => { db.Add(new Programme { Name = "meanwhile" }); await db.SaveChangesAsync(); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<WorkflowConcurrencyException>(() =>
            unit.AddOutboxAsync(new OutboxRecord { Id = "o1", Kind = OutboxKinds.Change, Payload = "{}", CreatedAt = DateTimeOffset.UtcNow }));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        // Right after the conflict a new unit begins with the write lock, so its retry queues instead of conflicting again.
        await using var retry = await App.Engine.Options.Store.BeginAsync();
        _ = await retry.GetSettingsAsync();
        await retry.AddOutboxAsync(new OutboxRecord { Id = "o1", Kind = OutboxKinds.Change, Payload = "{}", CreatedAt = DateTimeOffset.UtcNow });
        await retry.CommitAsync();
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_outbox_row()
    {
        await WalAsync();
        var store = App.Engine.Options.Store;
        var now = App.Clock.GetUtcNow();
        await using (var s = await store.BeginAsync())
        {
            for (var i = 0; i < 40; i++)
                await s.AddOutboxAsync(new OutboxRecord { Id = $"o{i:D2}", Kind = OutboxKinds.Change, Payload = "{}", CreatedAt = now.AddMilliseconds(i) });
            await s.CommitAsync();
        }
        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            for (var round = 0; round < 200 && claimed.Count < 40; round++)
            {
                try
                {
                    await using var s = await store.BeginAsync();
                    var rows = await s.ClaimOutboxAsync(now, now.AddMinutes(5), 3);
                    if (rows.Count == 0) return;
                    await Task.Delay(5);
                    await s.CommitAsync();
                    foreach (var r in rows) claimed.Add(r.Id);
                }
                catch (WorkflowConcurrencyException)
                {
                    await Task.Delay(10);
                }
            }
        })));
        Assert.Equal(40, claimed.Count);
        Assert.Equal(40, claimed.Distinct().Count());
    }
}

[Collection("postgres")]
public sealed class PostgresStoreEdgeCaseTests(PostgresFixture pg) : StoreEdgeCaseTests
{
    private readonly string _cs = pg.ConnectionString($"wf_{Guid.NewGuid():N}");

    protected override TestApp CreateApp(string? tenant) => new(o => o.UseNpgsql(_cs), tenant: tenant);
    protected override void UseDatabase(DbContextOptionsBuilder o) => o.UseNpgsql(_cs);
    protected override bool Sqlite => false;
    protected override string RefuseBoomSql => """
        CREATE FUNCTION no_boom() RETURNS trigger AS $$ BEGIN
          IF NEW."Notes" = 'boom' THEN RAISE EXCEPTION 'notes may not be boom'; END IF; RETURN NEW; END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER no_boom BEFORE UPDATE ON "Applications" FOR EACH ROW EXECUTE FUNCTION no_boom();
        """;
}
