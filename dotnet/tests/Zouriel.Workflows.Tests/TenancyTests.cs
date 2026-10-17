using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;

namespace Zouriel.Workflows.Tests;

/// <summary>Two engines with their own tenants sharing one store: each reads, verifies and purges only its own audit chain.</summary>
public sealed class TenancyTests
{
    private const string Def = """
    { "specVersion": 1, "id": "ID", "key": "KEY", "kind": "automation", "title": "T", "entity": "application", "trigger": { "type": "created" }, "steps": [] }
    """;

    private static WorkflowEngineOptions WithTenant(WorkflowEngineOptions o, string tenant) => new()
    {
        Registry = o.Registry, Store = o.Store, Identity = o.Identity, Notifications = o.Notifications, Webhooks = o.Webhooks, Actions = o.Actions,
        Time = o.Time, RecordLinkTemplate = o.RecordLinkTemplate, Tenant = tenant,
    };

    [Fact]
    public async Task Tenants_sharing_the_in_memory_store_read_purge_and_verify_only_their_own_chain()
    {
        var h = await AuditFixTests.HostAsync(o => WithTenant(o, "a"));
        var a = h.Engine;
        var b = new WorkflowEngine(WithTenant(a.Options, "b"));
        var admin = AuditFixTests.Admin;
        await a.CreateDefinitionAsync(Def.Replace("ID", "d1").Replace("KEY", "k1"), admin);
        await b.CreateDefinitionAsync(Def.Replace("ID", "d2").Replace("KEY", "k2"), admin);
        await b.CreateDefinitionAsync(Def.Replace("ID", "d3").Replace("KEY", "k3"), admin);

        // Both chains start at 1 in the same table and neither sees the other.
        Assert.Equal([1L], (await a.QueryAuditAsync(new AuditQuery())).Select(e => e.Seq));
        Assert.Equal([2L, 1L], (await b.QueryAuditAsync(new AuditQuery())).Select(e => e.Seq));
        Assert.All(await b.QueryAuditAsync(new AuditQuery { Tenant = "a" }), e => Assert.Equal("b", e.Tenant)); // the engine's tenant wins
        await using (var s = await h.Store.BeginAsync())
        {
            Assert.Empty(await s.QueryAuditAsync(new AuditQuery())); // the default chain is empty
            Assert.Single(await s.QueryAuditAsync(new AuditQuery { Tenant = "a" }));
        }
        Assert.True((await a.VerifyAuditAsync()).Valid);
        Assert.True((await b.VerifyAuditAsync()).Valid);

        // Tenant a purges its definition entries; tenant b's stay, and both chains still verify.
        var (settings, rev) = await a.GetSettingsAsync();
        settings.Audit = new AuditSettings { RetentionDays = new() { ["definition"] = 1 } };
        await a.SaveSettingsAsync(settings, rev, admin);
        h.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, await a.PurgeAuditAsync(admin));
        Assert.Empty(await a.QueryAuditAsync(new AuditQuery { Category = "definition" }));
        Assert.Equal(2, (await b.QueryAuditAsync(new AuditQuery { Category = "definition" })).Count);
        var checkA = await a.VerifyAuditAsync();
        Assert.True(checkA.Valid, checkA.Problem);
        var checkB = await b.VerifyAuditAsync();
        Assert.True(checkB.Valid, checkB.Problem);
    }
}
