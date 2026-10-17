using Zouriel.Workflows.Forms;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.Engine;

/// <summary>Everything the engine needs from the host app.</summary>
public sealed class WorkflowEngineOptions
{
    public required WorkflowRegistry Registry { get; init; }
    public required IWorkflowStore Store { get; init; }
    public IWorkflowIdentity Identity { get; init; } = new NoIdentity();
    public INotificationSender Notifications { get; init; } = new NoNotifications();
    public IWebhookClient Webhooks { get; init; } = new HttpWebhookClient();
    public IWorkflowSecretResolver Secrets { get; init; } = new NoSecrets();
    public IWorkflowActionResolver Actions { get; init; } = new ActivatorActionResolver();
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>The audit chain to write to (see <see cref="DependencyInjection.WorkflowEngineSettings.Tenant"/>).</summary>
    public string? Tenant { get; init; }
    /// <summary>Safety stop: the most steps one instance may run before it waits (protects against runaway loops).</summary>
    public int MaxStepsPerRun { get; init; } = 5000;
    /// <summary>When the engine sends its own task notifications ("you have a new task"). Null = first of inApp/email the registry lists.</summary>
    public string? TaskNotificationChannel { get; init; }
    public bool NotifyTaskAssignees { get; init; } = true;
    public IFormValidator FormValidator { get; init; } = new FormValidator();
    /// <summary>Who is making record writes outside workflows (e.g. the logged-in user of the request).</summary>
    public Changes.IWorkflowActorAccessor? ActorAccessor { get; init; }
    /// <summary>Set only for test runs: side effects (notifications, webhooks, actions) are logged here instead of happening.</summary>
    internal SimulationLog? SimulationLog { get; init; }
    /// <summary>A base URL for links to records in notifications, e.g. <c>https://app.example.com/records/{entity}/{id}</c>.</summary>
    public string? RecordLinkTemplate { get; init; }
    /// <summary>
    /// How long the change hook trusts its cached list of lifecycle-owned fields before reloading it. A
    /// node publishes and refreshes its own copy at once; other nodes see a newly owned field within this
    /// interval (writes on them are accepted, and observed, until then). Default 30 seconds.
    /// </summary>
    public TimeSpan OwnershipRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed class NoIdentity : IWorkflowIdentity
{
    public Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkflowUser?>(new WorkflowUser(id, id));
    public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<string?> GetManagerAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkflowUser>>([]);
}

internal sealed class NoNotifications : INotificationSender
{
    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoSecrets : IWorkflowSecretResolver
{
    public Task<string?> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>Creates action handlers with a parameterless constructor. ASP.NET apps use DI instead.</summary>
public sealed class ActivatorActionResolver : IWorkflowActionResolver
{
    public IWorkflowAction? Resolve(ActionDescriptor action) =>
        action.HandlerType is null ? null : Activator.CreateInstance(action.HandlerType) as IWorkflowAction;
}
