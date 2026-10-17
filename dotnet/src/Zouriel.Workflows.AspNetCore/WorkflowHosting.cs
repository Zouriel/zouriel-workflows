using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.DependencyInjection;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.AspNetCore;

/// <summary>How the logged-in user becomes a workflow actor.</summary>
public sealed class WorkflowUserOptions
{
    /// <summary>Claim holding the user id. Default: <c>sub</c>, then <see cref="ClaimTypes.NameIdentifier"/>.</summary>
    public Func<ClaimsPrincipal, string?> Id { get; set; } = p => p.FindFirstValue("sub") ?? p.FindFirstValue(ClaimTypes.NameIdentifier);
    public Func<ClaimsPrincipal, string?> Name { get; set; } = p => p.FindFirstValue("name") ?? p.Identity?.Name;
    public Func<ClaimsPrincipal, IReadOnlyList<string>> Roles { get; set; } = p => p.FindAll(ClaimTypes.Role).Concat(p.FindAll("role")).Select(c => c.Value).Distinct().ToList();
}

/// <summary>Reads the current request's user, so record writes outside workflows are attributed to them.</summary>
public sealed class HttpContextActorAccessor(IHttpContextAccessor http, WorkflowUserOptions options) : IWorkflowActorAccessor
{
    public WorkflowActor? Current
    {
        get
        {
            var user = http.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return null;
            var id = options.Id(user);
            return id is null ? null : WorkflowActor.User(id, options.Name(user), options.Roles(user));
        }
    }
}

public static class WorkflowHostingExtensions
{
    /// <summary>
    /// Registers workflows for an ASP.NET Core app: the engine, the registry you describe in
    /// <paramref name="configure"/>, the background worker (outbox, notifications, timers) and the
    /// pieces <c>MapWorkflowApi</c> needs.
    /// <code>
    /// builder.Services.AddWorkflows(wf => wf
    ///     .UseEntityFrameworkCore&lt;AppDbContext&gt;()
    ///     .Entity&lt;Application&gt;("application", e => e.Field(a => a.Status, f => f.LifecycleCapable()))
    ///     .UseIdentity&lt;AppWorkflowIdentity&gt;()
    ///     .UseNotifications&lt;AppNotificationSender&gt;());
    /// </code>
    /// </summary>
    public static WorkflowBuilder AddWorkflows(this IServiceCollection services, Action<WorkflowBuilder> configure, Action<WorkflowUserOptions>? users = null)
    {
        var userOptions = new WorkflowUserOptions();
        users?.Invoke(userOptions);
        services.AddHttpContextAccessor();
        services.TryAddSingleton(userOptions);
        services.TryAddSingleton<IWorkflowActorAccessor, HttpContextActorAccessor>();
        var builder = services.AddWorkflowsCore(configure);
        services.AddHostedService<WorkflowWorker>();
        return builder;
    }
}

/// <summary>Processes the outbox (change events, notifications) and due timers in the background.</summary>
public sealed class WorkflowWorker(WorkflowEngine engine, WorkflowEngineSettings settings, ILogger<WorkflowWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.RunWorker) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var busy = false;
            try
            {
                await engine.RefreshOwnershipAsync(stoppingToken);
                busy = await engine.ProcessOutboxAsync(100, stoppingToken) + await engine.ProcessTimersAsync(100, stoppingToken) > 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogError(e, "Workflow background work failed; retrying shortly.");
            }
            if (!busy)
            {
                try
                {
                    await Task.Delay(settings.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        await engine.FlushBackgroundAsync();
    }
}
