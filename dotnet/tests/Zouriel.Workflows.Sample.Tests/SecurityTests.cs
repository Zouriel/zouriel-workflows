using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zouriel.Workflows.AspNetCore;

namespace Zouriel.Workflows.Sample.Tests;

/// <summary>Who may do and see what through the API, and the request limits.</summary>
public sealed class SecurityTests : IClassFixture<SecurityTests.SecureApp>
{
    /// <summary>
    /// The sample app plus, for these tests: a principal without a user id (header <c>X-Test-NoId</c>), an
    /// access policy that hides chosen records from ali, and a settings policy nobody passes.
    /// </summary>
    public sealed class SecureApp : WebApplicationFactory<Program>
    {
        public string Db { get; } = Path.Combine(Path.GetTempPath(), $"wf-sec-{Guid.NewGuid():N}.db");
        public ConcurrentDictionary<string, bool> HiddenFromAli { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("Database", Db).UseSetting("Playground:Enabled", "false")
            .ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, NoIdHandler>(NoIdHandler.SchemeName, null)
                    .AddPolicyScheme("Test", "Test", o => o.ForwardDefaultSelector = ctx =>
                        ctx.Request.Headers.ContainsKey("X-Test-NoId") ? NoIdHandler.SchemeName : DemoAuthHandler.SchemeName);
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultScheme = "Test";
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                });
                services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("workflows.settings", p => p.RequireRole("nobody")));
                services.Replace(ServiceDescriptor.Singleton<IWorkflowAccessPolicy>(new HideFromAli(HiddenFromAli)));
            });
    }

    /// <summary>Authenticates anyone sending <c>X-Test-NoId</c>, with a name but no user id claim (like a client-credentials token).</summary>
    public sealed class NoIdHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "NoId";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("name", "Robot"), new Claim(ClaimTypes.Role, "admin")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class HideFromAli(ConcurrentDictionary<string, bool> hidden) : IWorkflowAccessPolicy
    {
        public ValueTask<bool> CanViewRecordAsync(WorkflowActor actor, string entity, string id, CancellationToken ct = default) =>
            ValueTask.FromResult(!(actor.Id == "ali" && hidden.ContainsKey(id)));
    }

    private readonly SecureApp _app;

    public SecurityTests(SecureApp app) => _app = app;

    private HttpClient As(string? user)
    {
        var c = _app.CreateClient();
        if (user is not null) c.DefaultRequestHeaders.Add("X-Demo-User", user);
        return c;
    }

    private async Task<int> NewApplicationAsync(string title)
    {
        var r = await As("sara").PostAsJsonAsync("/api/demo/applications", new { title, amount = 10, programmeId = 1 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var id = JsonNode.Parse(await r.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
        // Let the worker create the lifecycle instance.
        await Task.Delay(1500);
        return id;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())?["code"]?.GetValue<string>();

    [Fact]
    public async Task Anonymous_callers_get_401_everywhere()
    {
        var id = await NewApplicationAsync("Anonymous proof");
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/expire", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).GetAsync("/api/workflows/tasks?scope=all")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).GetAsync("/api/workflows/directory/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).PostAsync("/api/workflows/audit/purge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).PostAsJsonAsync("/api/workflows/signals/payment.received", new { entity = "application", entityId = id.ToString() })).StatusCode);
    }

    [Fact]
    public async Task A_principal_without_a_user_id_cannot_act_as_a_person()
    {
        var id = await NewApplicationAsync("No id proof");
        var robot = _app.CreateClient();
        robot.DefaultRequestHeaders.Add("X-Test-NoId", "1");
        var r = await robot.PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("notAllowed", await CodeAsync(r));
        Assert.Equal(HttpStatusCode.Forbidden, (await robot.PostAsJsonAsync("/api/workflows/manual/anything", new { })).StatusCode);
    }

    [Fact]
    public async Task People_cannot_apply_hidden_or_who_less_transitions()
    {
        var id = await NewApplicationAsync("Hidden proof");
        // "expire" is hidden and has no "who": only the system applies it, even for an admin.
        var r = await As("hana").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/expire", new { });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var record = JsonNode.Parse(await As("sara").GetStringAsync($"/api/workflows/records/application/{id}"))!;
        Assert.Equal("Draft", record["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Everyones_tasks_need_the_intervention_policy()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").GetAsync("/api/workflows/tasks?scope=all")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("hana").GetAsync("/api/workflows/tasks?scope=all")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("ali").GetAsync("/api/workflows/tasks")).StatusCode);
    }

    [Fact]
    public async Task The_access_policy_hides_records_their_instances_and_history()
    {
        var id = await NewApplicationAsync("Private proof");
        _app.HiddenFromAli[id.ToString()] = true;
        var instances = JsonNode.Parse(await As("hana").GetStringAsync($"/api/workflows/instances?entity=application&entityId={id}&kind=record"))!["items"]!.AsArray();
        var instanceId = instances.Single()!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").GetAsync($"/api/workflows/records/application/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").GetAsync($"/api/workflows/records/application/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").GetAsync($"/api/workflows/instances/{instanceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").GetAsync($"/api/workflows/instances/{instanceId}/definition")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").PostAsJsonAsync($"/api/workflows/records/application/{id}/transitions/submit", new { })).StatusCode);
        var listed = JsonNode.Parse(await As("ali").GetStringAsync($"/api/workflows/instances?entity=application&entityId={id}&kind=record"))!["items"]!.AsArray();
        Assert.Empty(listed);
        // Raw audit of an instance needs the audit policy.
        Assert.Equal(HttpStatusCode.Forbidden, (await As("sara").GetAsync($"/api/workflows/instances/{instanceId}/audit")).StatusCode);

        // Others still see it; history holds only lifecycle entries and decisions.
        var history = JsonNode.Parse(await As("sara").GetStringAsync($"/api/workflows/records/application/{id}/history"))!.AsArray();
        Assert.NotEmpty(history);
        Assert.All(history, h => Assert.True(h!["category"]!.GetValue<string>() == "lifecycle" || h["action"]!.GetValue<string>() == "decided"));
    }

    [Fact]
    public async Task Purging_needs_the_settings_policy_and_reading_settings_needs_edit()
    {
        // hana passes the audit policy but (in this app) not the settings policy.
        Assert.Equal(HttpStatusCode.OK, (await As("hana").PostAsync("/api/workflows/audit/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("hana").PostAsync("/api/workflows/audit/purge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("omar").GetAsync("/api/workflows/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("hana").GetAsync("/api/workflows/settings")).StatusCode);
    }

    [Fact]
    public async Task Signals_without_a_policy_need_the_intervention_policy()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ali").PostAsJsonAsync("/api/workflows/signals/payment.received", new { entity = "application", entityId = "1" })).StatusCode);
    }

    [Fact]
    public async Task Bodies_over_2_MB_are_refused_before_parsing()
    {
        var big = "{\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";
        var r = await As("hana").PostAsync("/api/workflows/validate", new StringContent(big, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("tooLarge", await CodeAsync(r));
        var create = await As("hana").PostAsync("/api/workflows/definitions", new StringContent(big, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, create.StatusCode);
    }

    [Fact]
    public async Task Test_runs_replay_at_most_50_events()
    {
        var def = JsonNode.Parse(await As("hana").GetStringAsync("/api/workflows/definitions/wf_finance"))!["draft"]!;
        var events = Enumerable.Range(0, 51).Select(_ => new { type = "advance", duration = "PT1M" }).ToArray();
        var r = await As("hana").PostAsJsonAsync("/api/workflows/simulate", new { definition = def, request = new { entityId = "2", events } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("tooManyEvents", await CodeAsync(r));
    }

    [Fact]
    public async Task A_duration_out_of_range_is_a_400_not_a_500()
    {
        var def = JsonNode.Parse(await As("hana").GetStringAsync("/api/workflows/definitions/wf_finance"))!["draft"]!;
        var r = await As("hana").PostAsJsonAsync("/api/workflows/simulate", new
        {
            definition = def, request = new { entityId = "2", events = new[] { new { type = "advance", duration = "P99999999999Y" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Decision_answers_over_256_KB_are_refused()
    {
        var r = await As("farah").PostAsJsonAsync("/api/workflows/tasks/any-task/decide", new { outcome = "approved", data = new { notes = new string('x', 257 * 1024) } });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("tooLarge", await CodeAsync(r));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-1", "\"'-1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tx", "\"'\tx\"")]
    [InlineData("plain", "\"plain\"")]
    public void Csv_cells_cannot_run_as_formulas(string value, string expected) => Assert.Equal(expected, WorkflowApi.Csv(value));

    [Fact]
    public void The_schema_leaves_out_fields_workflows_cannot_use()
    {
        var registry = new Registry.WorkflowRegistry
        {
            Entities =
            [
                new Registry.EntityDescriptor
                {
                    Key = "person", Label = "Person",
                    Fields =
                    [
                        new Registry.FieldDescriptor { Key = "name", Label = "Name" },
                        new Registry.FieldDescriptor { Key = "secretNote", Label = "Secret", Readable = false },
                        new Registry.FieldDescriptor { Key = "hiddenButSettable", Label = "Write only", Readable = false, Settable = true },
                    ],
                },
            ],
        };
        var fields = WorkflowApi.PublicRegistry(registry).Entities[0].Fields.Select(f => f.Key).ToList();
        Assert.Equal(["name", "hiddenButSettable"], fields);
    }
}
