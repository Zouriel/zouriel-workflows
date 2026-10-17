using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore.Tests;

public abstract class EfEngineTests : IAsyncLifetime
{
    protected TestApp App { get; private set; } = null!;

    protected abstract TestApp CreateApp();

    public async Task InitializeAsync()
    {
        App = CreateApp();
        await App.CreateDatabaseAsync();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();

    private const string NotifyOnChecked = """
    { "specVersion": 1, "id": "wf_chk", "key": "onChecked", "kind": "automation", "title": "On checked", "entity": "application",
      "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
      "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "notes", "value": { "calc": { "op": "concat", "args": [ { "const": "Checked" }, { "ref": "entity.title" }, { "ref": "entity.programme.name" } ] } } } ] } ] }
    """;

    private const string Lifecycle = """
    { "specVersion": 1, "id": "lc", "key": "review", "kind": "lifecycle", "title": "Review", "entity": "application",
      "lifecycle": { "field": "status", "states": [
          { "id": "d", "key": "Draft", "label": "Draft", "category": "initial" },
          { "id": "s", "key": "Submitted", "label": "Submitted", "category": "active" },
          { "id": "a", "key": "Approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [
          { "id": "t1", "key": "submit", "label": "Submit", "from": ["d"], "to": "s", "who": [ { "field": "entity.applicant" } ],
            "steps": [ { "id": "x", "type": "setFields", "values": [ { "field": "stage", "value": { "const": "checked" } } ] } ] },
          { "id": "t2", "key": "approve", "label": "Approve", "from": ["s"], "to": "a", "who": [ { "role": "reviewer" } ] } ] } }
    """;

    [Fact]
    public async Task A_committed_change_starts_the_workflow_and_its_write_is_saved()
    {
        await App.PublishAsync(NotifyOnChecked);
        var id = await App.WithDbAsync(async db =>
        {
            var p = new Programme { Name = "Engineering" };
            var a = new Application { Title = "Bursary", Stage = "new", Programme = p };
            db.Add(a);
            await db.SaveChangesAsync();
            return a.Id;
        });
        await App.WithDbAsync(async db =>
        {
            var a = await db.Applications.FindAsync(id);
            a!.Stage = "checked";
            await db.SaveChangesAsync();
        });
        await App.SettleAsync();
        var notes = await App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Notes).FirstAsync());
        Assert.Equal("Checked Bursary Engineering", notes);
        var instances = await App.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = "onChecked" });
        Assert.Equal("completed", Assert.Single(instances.Items).Status);
    }

    [Fact]
    public async Task A_rolled_back_change_starts_nothing()
    {
        await App.PublishAsync(NotifyOnChecked);
        var id = await App.WithDbAsync(async db =>
        {
            var a = new Application { Title = "Bursary", Stage = "new" };
            db.Add(a);
            await db.SaveChangesAsync();
            return a.Id;
        });
        await App.WithDbAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var a = await db.Applications.FindAsync(id);
            a!.Stage = "checked";
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        });
        await App.SettleAsync();
        Assert.Empty((await App.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = "onChecked" })).Items);
        Assert.Null(await App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Notes).FirstAsync()));
    }

    [Fact]
    public async Task Lifecycles_set_the_initial_state_refuse_outside_writes_and_apply_transitions()
    {
        await App.PublishAsync(Lifecycle);
        var id = await App.WithDbAsync(async db =>
        {
            var a = new Application { Title = "Grant", ApplicantId = "ali", Stage = "new" };
            db.Add(a);
            await db.SaveChangesAsync();
            Assert.Equal(ApplicationStatus.Draft, a.Status);
            return a.Id;
        });
        await App.SettleAsync();
        var refused = await Assert.ThrowsAsync<WorkflowFieldOwnedException>(() => App.WithDbAsync(async db =>
        {
            var a = await db.Applications.FindAsync(id);
            a!.Status = ApplicationStatus.Approved;
            await db.SaveChangesAsync();
        }));
        Assert.Equal("status", refused.Field);
        Assert.Equal(ApplicationStatus.Draft, await App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Status).FirstAsync()));

        var view = await App.Engine.ApplyTransitionAsync("application", id.ToString(), "submit", null, WorkflowActor.User("ali", "Ali"));
        Assert.Equal("Submitted", view.State);
        var saved = await App.WithDbAsync(db => db.Applications.AsNoTracking().FirstAsync(a => a.Id == id));
        Assert.Equal(ApplicationStatus.Submitted, saved.Status);
        Assert.Equal("checked", saved.Stage);
        await Assert.ThrowsAsync<WorkflowException>(() => App.Engine.ApplyTransitionAsync("application", id.ToString(), "approve", null, WorkflowActor.User("ali", "Ali")));
        var done = await App.Engine.ApplyTransitionAsync("application", id.ToString(), "approve", null, WorkflowActor.User("rev1", "Rana", ["reviewer"]));
        Assert.Equal("completed", done.Status);
        await App.Engine.FlushBackgroundAsync();
        var audit = await App.Engine.QueryAuditAsync(new AuditQuery { Category = "lifecycle", Take = 50 });
        Assert.Contains(audit, a => a.Action == "writeRefused");
        Assert.Equal(2, audit.Count(a => a.Action == "transitioned"));
        Assert.True((await App.Engine.VerifyAuditAsync()).Valid);
    }

    [Fact]
    public async Task Approvals_wait_on_durable_timers_and_time_out()
    {
        await App.PublishAsync("""
        { "specVersion": 1, "id": "wf_ap", "key": "approve", "kind": "automation", "title": "Approve", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
          "steps": [ { "id": "ap", "type": "approval", "assignees": [ { "role": "reviewer" } ], "rule": { "type": "any" }, "due": "P1D",
            "outcomes": { "approved": [], "rejected": [], "timedOut": [ { "id": "t", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "late" } } ] } ] } } ] }
        """);
        var id = await App.WithDbAsync(async db =>
        {
            var a = new Application { Title = "T", Stage = "new" };
            db.Add(a);
            await db.SaveChangesAsync();
            a.Stage = "checked";
            await db.SaveChangesAsync();
            return a.Id;
        });
        await App.SettleAsync();
        var tasks = await App.Engine.QueryTasksAsync(new TaskQuery { AssigneeId = "rev1", Statuses = ["open"] }, null);
        Assert.Single(tasks.Items);
        App.Clock.Advance(TimeSpan.FromDays(1));
        await App.SettleAsync();
        Assert.Equal("late", await App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Notes).FirstAsync()));
        Assert.Empty((await App.Engine.QueryTasksAsync(new TaskQuery { Statuses = ["open"] }, null)).Items);
    }

    [Fact]
    public async Task For_each_reads_related_records_and_writes_back()
    {
        await App.PublishAsync("""
        { "specVersion": 1, "id": "wf_fe", "key": "verify", "kind": "automation", "title": "Verify", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
          "steps": [ { "id": "fe", "type": "forEach", "collection": "entity.documents", "max": 10, "steps": [
            { "id": "v", "type": "setFields", "target": "self", "values": [ { "field": "amount", "value": { "calc": { "op": "sum", "args": [ { "ref": "entity.amount" }, { "const": 1 } ] } } } ] } ] } ] }
        """);
        var id = await App.WithDbAsync(async db =>
        {
            var a = new Application { Title = "Docs", Stage = "new", Documents = [new Document { Name = "A" }, new Document { Name = "B" }] };
            db.Add(a);
            await db.SaveChangesAsync();
            a.Stage = "checked";
            await db.SaveChangesAsync();
            return a.Id;
        });
        await App.SettleAsync();
        Assert.Equal(2m, await App.WithDbAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Amount).FirstAsync()));
    }
}

public sealed class SqliteEngineTests : EfEngineTests, IDisposable
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
public sealed class PostgresEngineTests(PostgresFixture pg) : EfEngineTests
{
    protected override TestApp CreateApp()
    {
        var cs = pg.ConnectionString($"wf_{Guid.NewGuid():N}");
        return new TestApp(o => o.UseNpgsql(cs));
    }

    [Fact]
    public async Task Concurrent_writers_keep_one_audit_chain_and_start_each_trigger_once()
    {
        await App.PublishAsync("""
        { "specVersion": 1, "id": "wf_c", "key": "count", "kind": "automation", "title": "Count", "entity": "application",
          "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
          "steps": [ { "id": "s", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "seen" } } ] } ] }
        """);
        var ids = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await App.WithDbAsync(async db =>
            {
                var a = new Application { Title = $"A{i}", Stage = "new" };
                db.Add(a);
                await db.SaveChangesAsync();
                a.Stage = "checked";
                await db.SaveChangesAsync();
                return a.Id;
            }));
        }
        // Four workers drain the outbox at the same time.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(App.SettleAsync)));
        await App.SettleAsync();
        var instances = await App.Engine.QueryInstancesAsync(new InstanceQuery { DefinitionKey = "count", Take = 100 });
        Assert.Equal(20, instances.Items.Count);
        Assert.Equal(20, instances.Items.Select(x => x.EntityId).Distinct().Count());
        var check = await App.Engine.VerifyAuditAsync();
        Assert.True(check.Valid, check.Problem);
    }
}
