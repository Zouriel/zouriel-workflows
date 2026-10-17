using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore.Tests;

/// <summary>The EF store and change interceptor under the conditions real apps create, on every provider.</summary>
public abstract class StoreBehaviourTests : IAsyncLifetime
{
    protected TestApp App { get; private set; } = null!;

    protected abstract TestApp CreateApp();

    public async Task InitializeAsync()
    {
        App = CreateApp();
        await App.CreateDatabaseAsync();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();

    protected const string Lifecycle = """
    { "specVersion": 1, "id": "lc", "key": "review", "kind": "lifecycle", "title": "Review", "entity": "application",
      "lifecycle": { "field": "status", "mode": "MODE", "states": [
          { "id": "d", "key": "Draft", "label": "Draft", "category": "initial" },
          { "id": "s", "key": "Submitted", "label": "Submitted", "category": "active" },
          { "id": "a", "key": "Approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [
          { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "field": "entity.applicant" } ] } ] } }
    """;

    protected const string OnChecked = """
    { "specVersion": 1, "id": "wf_chk", "key": "onChecked", "kind": "automation", "title": "On checked", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
      "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "seen" } } ] } ] }
    """;

    /// <summary>Runs synchronous EF code (SaveChanges, Find) in a fresh scope, as a non-async app would.</summary>
    private T WithDbSync<T>(Func<AppDb, T> work)
    {
        using var scope = App.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    private Task<int> NewApplicationAsync(string? stage = "new") => App.WithDbAsync(async db =>
    {
        var a = new Application { Title = "G", ApplicantId = "ali", Stage = stage };
        db.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    });

    private Task<ApplicationStatus?> StatusAsync(int id) => App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Status).FirstAsync());

    private Task<int> ChangeEventsAsync() => App.WithDbAsync(db => db.Set<OutboxRecord>().CountAsync(o => o.Kind == OutboxKinds.Change && o.Payload.Contains("\"updated\"")));

    [Fact]
    public async Task A_disconnected_update_of_an_owned_field_is_refused()
    {
        await App.PublishAsync(Lifecycle.Replace("MODE", "enforce"));
        var id = await NewApplicationAsync();
        await App.SettleAsync();
        await Assert.ThrowsAsync<WorkflowFieldOwnedException>(() => App.WithDbAsync(async db =>
        {
            db.Update(new Application { Id = id, Title = "G", ApplicantId = "ali", Status = ApplicationStatus.Approved });
            await db.SaveChangesAsync();
        }));
        Assert.Equal(ApplicationStatus.Draft, await StatusAsync(id));
        // The same through the synchronous path.
        Assert.Throws<WorkflowFieldOwnedException>(() => WithDbSync(db =>
        {
            db.Update(new Application { Id = id, Title = "G", ApplicantId = "ali", Status = ApplicationStatus.Approved });
            return db.SaveChanges();
        }));
        Assert.Equal(ApplicationStatus.Draft, await StatusAsync(id));
    }

    [Fact]
    public async Task A_disconnected_update_of_a_watched_field_starts_its_trigger()
    {
        await App.PublishAsync(OnChecked);
        var id = await NewApplicationAsync();
        await App.WithDbAsync(async db =>
        {
            db.Update(new Application { Id = id, Title = "G", ApplicantId = "ali", Stage = "checked", Status = ApplicationStatus.Draft });
            await db.SaveChangesAsync();
        });
        await App.SettleAsync();
        Assert.Single((await App.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = "onChecked" })).Items);
    }

    [Fact]
    public async Task The_synchronous_SaveChanges_path_records_creates_and_changes_and_refuses_owned_writes()
    {
        await App.PublishAsync(Lifecycle.Replace("MODE", "enforce"));
        await App.PublishAsync(OnChecked);
        var id = WithDbSync(db =>
        {
            var a = new Application { Title = "Sync", ApplicantId = "ali", Stage = "new" };
            db.Add(a);
            db.SaveChanges();
            a.Stage = "checked";
            db.SaveChanges();
            return a.Id;
        });
        await App.SettleAsync();
        Assert.Equal(ApplicationStatus.Draft, await StatusAsync(id));
        Assert.Single((await App.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = "onChecked" })).Items);
        Assert.Throws<WorkflowFieldOwnedException>(() => WithDbSync(db =>
        {
            var a = db.Applications.Find(id)!;
            a.Status = ApplicationStatus.Approved;
            return db.SaveChanges();
        }));
    }

    [Fact]
    public async Task A_failed_save_that_is_retried_writes_its_change_event_once()
    {
        var id = await NewApplicationAsync();
        await App.WithDbAsync(async db =>
        {
            var a = await db.Applications.FindAsync(id);
            a!.Stage = "checked";
            db.Add(new Programme { Id = 999, Name = "p" });
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Programmes\" (\"Id\", \"Name\") VALUES (999, 'x')");
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
            // The app fixes the problem and saves again.
            db.Entry(db.Set<Programme>().Local.First(p => p.Id == 999)).State = EntityState.Detached;
            await db.SaveChangesAsync();
        });
        Assert.Equal(1, await ChangeEventsAsync());
    }

    [Fact]
    public async Task Subclasses_of_a_registered_entity_are_watched_and_owned()
    {
        await App.PublishAsync(Lifecycle.Replace("MODE", "enforce"));
        await App.PublishAsync(OnChecked);
        var id = await App.WithDbAsync(async db =>
        {
            var a = new UrgentApplication { Title = "U", ApplicantId = "ali", Stage = "new", Reason = "deadline" };
            db.Add(a);
            await db.SaveChangesAsync();
            Assert.Equal(ApplicationStatus.Draft, a.Status);
            a.Stage = "checked";
            await db.SaveChangesAsync();
            return a.Id;
        });
        await App.SettleAsync();
        Assert.Single((await App.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = "onChecked" })).Items);
        await Assert.ThrowsAsync<WorkflowFieldOwnedException>(() => App.WithDbAsync(async db =>
        {
            var a = await db.Applications.FindAsync(id);
            a!.Status = ApplicationStatus.Approved;
            await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Observe_mode_accepts_outside_writes_and_records_the_matching_transition()
    {
        await App.PublishAsync(Lifecycle.Replace("MODE", "observe"));
        var id = await NewApplicationAsync();
        await App.SettleAsync();
        await App.WithDbAsync(async db =>
        {
            var a = await db.Applications.FindAsync(id);
            a!.Status = ApplicationStatus.Submitted;
            await db.SaveChangesAsync();
        });
        await App.SettleAsync();
        Assert.Equal(ApplicationStatus.Submitted, await StatusAsync(id));
        var record = await App.Engine.GetRecordAsync("application", id.ToString(System.Globalization.CultureInfo.InvariantCulture), WorkflowActor.User("ali", "Ali"));
        Assert.Equal("Submitted", record.State);
        var audit = await App.Engine.QueryAuditAsync(new AuditQuery { Category = "lifecycle", Take = 50 });
        Assert.Contains(audit, a => a.Action == "writeObserved");
        Assert.Contains(audit, a => a.Action == "transitioned");
    }

    [Fact]
    public async Task Purging_the_newest_entry_keeps_the_chain_going_and_verifiable()
    {
        var admin = WorkflowActor.User("admin", "Admin");
        await App.Engine.CreateDefinitionAsync(Lifecycle.Replace("MODE", "enforce"), admin);
        var (s, rev) = await App.Engine.GetSettingsAsync();
        s.Audit = new AuditSettings { RetentionDays = new() { ["settings"] = 1 } };
        await App.Engine.SaveSettingsAsync(s, rev, admin); // the newest entry: settings.updated
        App.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, await App.Engine.PurgeAuditAsync(admin));
        await App.Engine.CreateDefinitionAsync(OnChecked, admin);
        var seqs = await App.WithDbAsync(db => db.Set<AuditRecord>().AsNoTracking().OrderBy(a => a.Seq).Select(a => a.Seq).ToListAsync());
        Assert.Equal([1L, 3L, 4L], seqs);
        var check = await App.Engine.VerifyAuditAsync();
        Assert.True(check.Valid, check.Problem);
        Assert.Equal([(2L, 2L)], check.PurgedGaps);
    }

    [Fact]
    public async Task A_claimed_timer_is_skipped_while_its_lease_lasts_and_claimed_again_after()
    {
        var now = App.Clock.GetUtcNow();
        var store = App.Engine.Options.Store;
        await using (var s = await store.BeginAsync())
        {
            await s.AddBookmarkAsync(new BookmarkRecord { Id = "b1", Kind = BookmarkKinds.Timer, Purpose = "wait", DueAt = now.AddMinutes(-1), CreatedAt = now });
            await s.CommitAsync();
        }
        async Task<int> ClaimAsync(DateTimeOffset at)
        {
            await using var s = await store.BeginAsync();
            var n = (await s.ClaimDueBookmarksAsync(at, at.AddMinutes(2), 10)).Count;
            await s.CommitAsync();
            return n;
        }
        Assert.Equal(1, await ClaimAsync(now));
        Assert.Equal(0, await ClaimAsync(now.AddMinutes(1)));
        Assert.Equal(1, await ClaimAsync(now.AddMinutes(3)));
    }

    [Fact]
    public async Task A_unique_violation_is_a_race_only_on_workflow_rows()
    {
        await App.WithDbAsync(async db =>
        {
            db.Add(new Programme { Id = 7, Name = "a" });
            await db.SaveChangesAsync();
        });
        var host = await Assert.ThrowsAnyAsync<DbUpdateException>(() => App.WithDbAsync(async db =>
        {
            db.Add(new Programme { Id = 7, Name = "b" });
            await db.SaveChangesAsync();
        }));
        Assert.True(DbErrors.IsUniqueViolation(host));
        Assert.Null(DbErrors.ToConcurrency(host));
        await App.WithDbAsync(async db =>
        {
            db.Add(new TriggerClaimRecord { DefinitionId = "d", EventId = "e" });
            await db.SaveChangesAsync();
        });
        var claim = await Assert.ThrowsAnyAsync<DbUpdateException>(() => App.WithDbAsync(async db =>
        {
            db.Add(new TriggerClaimRecord { DefinitionId = "d", EventId = "e" });
            await db.SaveChangesAsync();
        }));
        Assert.NotNull(DbErrors.ToConcurrency(claim));
    }

    [Fact]
    public async Task Rolling_back_to_a_savepoint_undoes_saved_and_unsaved_app_writes()
    {
        var id = await NewApplicationAsync();
        await using var s = await App.Engine.Options.Store.BeginAsync();
        var db = s.Services!.GetRequiredService<AppDb>();
        var a = await db.Applications.FindAsync(id);
        Assert.True(await s.CreateSavepointAsync("action"));
        a!.Notes = "half done";
        db.Add(new Programme { Name = "created by a failed action" });
        await db.SaveChangesAsync();
        a.Amount = 99;
        await s.RollbackToSavepointAsync("action");
        Assert.Null(a.Notes);
        Assert.Equal(0m, a.Amount);
        Assert.Empty(db.ChangeTracker.Entries<Programme>());
        await s.CommitAsync();
        var saved = await App.WithDbAsync(x => x.Applications.AsNoTracking().FirstAsync(r => r.Id == id));
        Assert.Null(saved.Notes);
        Assert.Equal(0, await App.WithDbAsync(x => x.Programmes.CountAsync()));
    }

    [Fact]
    public async Task Cleanup_deletes_old_processed_outbox_rows_and_trigger_claims()
    {
        var now = App.Clock.GetUtcNow();
        await App.WithDbAsync(async db =>
        {
            db.Add(new OutboxRecord { Id = "old", CreatedAt = now.AddDays(-9), ProcessedAt = now.AddDays(-8) });
            db.Add(new OutboxRecord { Id = "recent", CreatedAt = now.AddDays(-1), ProcessedAt = now.AddDays(-1) });
            db.Add(new OutboxRecord { Id = "open", CreatedAt = now.AddDays(-20) });
            db.Add(new TriggerClaimRecord { DefinitionId = "d", EventId = "old", ClaimedAt = now.AddDays(-31) });
            db.Add(new TriggerClaimRecord { DefinitionId = "d", EventId = "new", ClaimedAt = now.AddDays(-2) });
            await db.SaveChangesAsync();
        });
        Assert.Equal(2, await App.Engine.CleanupAsync(TimeSpan.FromDays(7), TimeSpan.FromDays(30)));
        Assert.Equal(["open", "recent"], await App.WithDbAsync(db => db.Set<OutboxRecord>().Select(o => o.Id).OrderBy(x => x).ToListAsync()));
        Assert.Equal(["new"], await App.WithDbAsync(db => db.Set<TriggerClaimRecord>().Select(c => c.EventId).ToListAsync()));
    }

    [Fact]
    public async Task Trigger_claims_are_stamped_with_the_engine_clock()
    {
        await using (var s = await App.Engine.Options.Store.BeginAsync())
        {
            Assert.True(await s.TryClaimTriggerEventAsync("d", "e1"));
            await s.CommitAsync();
        }
        Assert.Equal(App.Clock.GetUtcNow(), await App.WithDbAsync(db => db.Set<TriggerClaimRecord>().Select(c => c.ClaimedAt).FirstAsync()));
    }

    [Fact]
    public async Task Ids_page_in_key_order_for_int_guid_and_string_keys()
    {
        var appIds = new List<string>();
        for (var i = 0; i < 5; i++) appIds.Add((await NewApplicationAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var docIds = await App.WithDbAsync(async db =>
        {
            var a = new Application { Title = "D", Documents = [.. Enumerable.Range(0, 5).Select(i => new Document { Name = $"d{i}" })] };
            db.Add(a);
            db.AddRange(Enumerable.Range(0, 5).Select(i => new Tag { Id = $"tag-{i}", Name = $"t{i}" }));
            await db.SaveChangesAsync();
            return a.Documents.Select(d => d.Id).ToList();
        });
        appIds.Add(await App.WithDbAsync(db => db.Applications.Where(a => a.Title == "D").Select(a => a.Id.ToString()).FirstAsync()));

        async Task<List<string>> AllAsync(string entity)
        {
            await using var s = await App.Engine.Options.Store.BeginAsync();
            var all = new List<string>();
            string? after = null;
            while (true)
            {
                var page = await s.Entities.ListIdsAsync(entity, after, 2);
                all.AddRange(page);
                if (page.Count < 2) return all;
                after = page[^1];
            }
        }

        Assert.Equal(appIds.Select(int.Parse).Order().Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), await AllAsync("application"));
        Assert.Equal(["tag-0", "tag-1", "tag-2", "tag-3", "tag-4"], await AllAsync("tag"));
        var docs = await AllAsync("document");
        Assert.Equal(5, docs.Distinct().Count());
        Assert.Equal(docIds.Select(g => g.ToString()).Order(), docs.Order());
    }
}

public sealed class SqliteStoreBehaviourTests : StoreBehaviourTests, IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"wf-{Guid.NewGuid():N}.db");

    protected override TestApp CreateApp() => new(o => o.UseSqlite($"Data Source={_file}"), sqlite: true);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
    }
}

[Collection("postgres")]
public sealed class PostgresStoreBehaviourTests(PostgresFixture pg) : StoreBehaviourTests
{
    private string _cs = "";

    protected override TestApp CreateApp()
    {
        _cs = pg.ConnectionString($"wf_{Guid.NewGuid():N}");
        return new TestApp(o => o.UseNpgsql(_cs));
    }

    [Fact]
    public async Task Concurrent_units_writing_audit_form_one_unbroken_chain()
    {
        var admin = WorkflowActor.User("admin", "Admin");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
                await App.Engine.CreateDefinitionAsync(OnChecked.Replace("wf_chk", $"wf_{w}_{i}").Replace("onChecked", $"k{w}x{i}"), admin);
        })));
        var seqs = await App.WithDbAsync(db => db.Set<AuditRecord>().AsNoTracking().OrderBy(a => a.Seq).Select(a => a.Seq).ToListAsync());
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), seqs);
        var check = await App.Engine.VerifyAuditAsync();
        Assert.True(check.Valid, check.Problem);
    }

    [Fact]
    public async Task With_a_retrying_execution_strategy_new_records_need_the_callers_transaction()
    {
        await App.PublishAsync(OnChecked);
        var interceptor = App.Services.GetRequiredService<WorkflowSaveChangesInterceptor>();
        var options = new DbContextOptionsBuilder<AppDb>().UseNpgsql(_cs, x => x.EnableRetryOnFailure()).AddInterceptors(interceptor).Options;
        await using (var db = new AppDb(options))
        {
            db.Add(new Application { Title = "R" });
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("CreateExecutionStrategy", e.Message, StringComparison.Ordinal);
        }
        await using (var db = new AppDb(options))
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                db.Add(new Application { Title = "R" });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
        }
        Assert.Equal(1, await App.WithDbAsync(db => db.Set<OutboxRecord>().CountAsync(o => o.Payload.Contains("\"created\""))));
    }
}
