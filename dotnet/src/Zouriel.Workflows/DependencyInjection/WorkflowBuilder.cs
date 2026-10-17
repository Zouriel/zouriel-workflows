using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Forms;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.DependencyInjection;

/// <summary>Settings for the engine and its background workers.</summary>
public sealed class WorkflowEngineSettings
{
    /// <summary>
    /// The audit chain this app writes to, when several apps or tenants share one audit table: each tenant
    /// value has its own hash chain. It does not separate any other data; use separate databases or
    /// schemas for that.
    /// </summary>
    public string? Tenant { get; set; }
    /// <summary>Links in notifications, e.g. <c>https://app.example.com/{entity}/{id}</c>.</summary>
    public string? RecordLinkTemplate { get; set; }
    /// <summary>Channel for "you have a new task" messages. Null = inApp, else email.</summary>
    public string? TaskNotificationChannel { get; set; }
    public bool NotifyTaskAssignees { get; set; } = true;
    /// <summary>The most steps one instance may run in one go before it waits (stops runaway loops).</summary>
    public int MaxStepsPerRun { get; set; } = 5000;
    /// <summary>See <see cref="WorkflowEngineOptions.OwnershipRefreshInterval"/>.</summary>
    public TimeSpan OwnershipRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Processed outbox rows older than this are deleted by the worker. Default 7 days.</summary>
    public TimeSpan KeepProcessedOutbox { get; set; } = TimeSpan.FromDays(7);
    /// <summary>How the default webhook client guards the network (private addresses are blocked unless allowed).</summary>
    public HttpWebhookClientOptions Webhooks { get; set; } = new();
    /// <summary>Trigger claims (start dedupe rows) older than this are deleted by the worker. Default 30 days.</summary>
    public TimeSpan KeepTriggerClaims { get; set; } = TimeSpan.FromDays(30);
    /// <summary>How often the worker checks the outbox and due timers.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Turn off to run the background work yourself (e.g. in a separate worker process).</summary>
    public bool RunWorker { get; set; } = true;
    public TimeProvider Time { get; set; } = TimeProvider.System;
}

/// <summary>
/// Configures workflows for an app: what records workflows may use (the registry), where state is
/// stored, and the app services the engine calls (people, notifications, secrets).
/// </summary>
public sealed class WorkflowBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
    public RegistryBuilder Registry { get; } = new();
    public WorkflowEngineSettings Settings { get; } = new();
    internal Func<IServiceProvider, WorkflowEngine, IWorkflowStore>? StoreFactory { get; private set; }

    /// <summary>Uses a custom store (EF Core users call <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> instead).</summary>
    public WorkflowBuilder UseStore(Func<IServiceProvider, WorkflowEngine, IWorkflowStore> factory)
    {
        StoreFactory = factory;
        return this;
    }

    /// <summary>Keeps workflow state in memory (demos and tests; nothing survives a restart).</summary>
    public WorkflowBuilder UseInMemoryStore(InMemoryWorkflowStore? store = null)
    {
        Services.TryAddSingleton(sp => store ?? new InMemoryWorkflowStore());
        return UseStore((sp, engine) =>
        {
            var s = sp.GetRequiredService<InMemoryWorkflowStore>();
            s.Registry ??= engine.Registry;
            s.Changes ??= engine.Changes;
            return s;
        });
    }

    public WorkflowBuilder Entity<T>(string key, Action<EntityBuilder<T>> configure) where T : class
    {
        Registry.Entity(key, configure);
        return this;
    }

    /// <summary>Registers an app action. The handler is created from DI each time the step runs.</summary>
    public WorkflowBuilder Action<THandler>(string key, Action<ActionBuilder>? configure = null) where THandler : class, IWorkflowAction
    {
        Registry.Action<THandler>(key, configure);
        Services.TryAddScoped<THandler>();
        return this;
    }

    public WorkflowBuilder Signal(string key, Action<SignalBuilder>? configure = null)
    {
        Registry.Signal(key, configure);
        return this;
    }

    public WorkflowBuilder Role(string key, string label)
    {
        Registry.Role(key, label);
        return this;
    }

    public WorkflowBuilder Group(string key, string label)
    {
        Registry.Group(key, label);
        return this;
    }

    public WorkflowBuilder Channel(string key, string label)
    {
        Registry.Channel(key, label);
        return this;
    }

    /// <summary>Where users, roles, groups and managers come from (scoped services are fine).</summary>
    public WorkflowBuilder UseIdentity<T>() where T : class, IWorkflowIdentity
    {
        Services.TryAddScoped<T>();
        Services.Replace(ServiceDescriptor.Singleton<IWorkflowIdentity>(sp => new ScopedIdentity<T>(sp)));
        return this;
    }

    public WorkflowBuilder UseNotifications<T>() where T : class, INotificationSender
    {
        Services.TryAddScoped<T>();
        Services.Replace(ServiceDescriptor.Singleton<INotificationSender>(sp => new ScopedNotifications<T>(sp)));
        return this;
    }

    public WorkflowBuilder UseSecrets<T>() where T : class, IWorkflowSecretResolver
    {
        Services.TryAddScoped<T>();
        Services.Replace(ServiceDescriptor.Singleton<IWorkflowSecretResolver>(sp => new ScopedSecrets<T>(sp)));
        return this;
    }

    public WorkflowBuilder UseWebhookClient<T>() where T : class, IWebhookClient
    {
        Services.Replace(ServiceDescriptor.Singleton<IWebhookClient, T>());
        return this;
    }

    public WorkflowBuilder Configure(Action<WorkflowEngineSettings> configure)
    {
        configure(Settings);
        return this;
    }
}

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the workflow engine (as a singleton) and its registry. Hosts normally call
    /// <c>AddWorkflows</c> from <c>Zouriel.Workflows.AspNetCore</c>, which also adds the API and the worker.
    /// </summary>
    public static WorkflowBuilder AddWorkflowsCore(this IServiceCollection services, Action<WorkflowBuilder> configure)
    {
        var builder = new WorkflowBuilder(services);
        configure(builder);
        if (builder.StoreFactory is null) throw new InvalidOperationException("Choose where workflows are stored: call UseEntityFrameworkCore<TContext>() (or UseInMemoryStore() for demos).");
        var registry = builder.Registry.Build();
        services.AddSingleton(registry);
        services.AddSingleton(builder.Settings);
        services.TryAddSingleton<IWorkflowIdentity, NoIdentityService>();
        services.TryAddSingleton<INotificationSender, NoNotificationService>();
        services.TryAddSingleton<IWorkflowSecretResolver, NoSecretService>();
        services.TryAddSingleton<IWebhookClient>(sp => new HttpWebhookClient(sp.GetRequiredService<WorkflowEngineSettings>().Webhooks));
        services.TryAddSingleton<IFormValidator, FormValidator>();
        services.TryAddSingleton<IWorkflowActionResolver, ServiceProviderActionResolver>();
        services.AddSingleton(sp =>
        {
            var s = sp.GetRequiredService<WorkflowEngineSettings>();
            WorkflowEngine? engine = null;
            var store = new LazyStore(() => builder.StoreFactory(sp, engine!));
            engine = new WorkflowEngine(new WorkflowEngineOptions
            {
                Registry = registry, Store = store, Identity = sp.GetRequiredService<IWorkflowIdentity>(),
                Notifications = sp.GetRequiredService<INotificationSender>(), Webhooks = sp.GetRequiredService<IWebhookClient>(),
                Secrets = sp.GetRequiredService<IWorkflowSecretResolver>(), Actions = sp.GetRequiredService<IWorkflowActionResolver>(),
                Time = s.Time, Tenant = s.Tenant, MaxStepsPerRun = s.MaxStepsPerRun, TaskNotificationChannel = s.TaskNotificationChannel,
                NotifyTaskAssignees = s.NotifyTaskAssignees, FormValidator = sp.GetRequiredService<IFormValidator>(),
                RecordLinkTemplate = s.RecordLinkTemplate, ActorAccessor = sp.GetService<IWorkflowActorAccessor>(),
                OwnershipRefreshInterval = s.OwnershipRefreshInterval,
            });
            return engine;
        });
        return builder;
    }

    /// <summary>Defers creating the store until first use (the store needs the engine for change inspection).</summary>
    private sealed class LazyStore(Func<IWorkflowStore> create) : IWorkflowStore
    {
        private readonly Lazy<IWorkflowStore> _store = new(create);
        public Task<IWorkflowSession> BeginAsync(CancellationToken ct = default) => _store.Value.BeginAsync(ct);
    }
}

/// <summary>
/// The DI scope engine work runs in. Stores set it for each unit of work so app services the
/// engine calls (identity, notifications, actions) are resolved from the same scope, and so share
/// the same DbContext as the workflow state.
/// </summary>
public static class WorkflowServiceScope
{
    private static readonly AsyncLocal<IServiceProvider?> CurrentProvider = new();

    public static IServiceProvider? Current
    {
        get => CurrentProvider.Value;
        set => CurrentProvider.Value = value;
    }

    /// <summary>Runs <paramref name="use"/> with a service resolved from the current scope, or from a short-lived scope.</summary>
    public static async Task<TResult> UseAsync<TService, TResult>(IServiceProvider root, Func<TService, Task<TResult>> use) where TService : notnull
    {
        if (Current is { } scoped) return await use(scoped.GetRequiredService<TService>());
        await using var scope = root.CreateAsyncScope();
        return await use(scope.ServiceProvider.GetRequiredService<TService>());
    }
}

internal sealed class ScopedIdentity<T>(IServiceProvider root) : IWorkflowIdentity where T : class, IWorkflowIdentity
{
    public Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default) => WorkflowServiceScope.UseAsync<T, WorkflowUser?>(root, s => s.GetUserAsync(id, ct));
    public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default) => WorkflowServiceScope.UseAsync<T, IReadOnlyList<string>>(root, s => s.GetUsersInRoleAsync(role, ct));
    public Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default) => WorkflowServiceScope.UseAsync<T, IReadOnlyList<string>>(root, s => s.GetUsersInGroupAsync(group, ct));
    public Task<string?> GetManagerAsync(string userId, CancellationToken ct = default) => WorkflowServiceScope.UseAsync<T, string?>(root, s => s.GetManagerAsync(userId, ct));
    public Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default) => WorkflowServiceScope.UseAsync<T, IReadOnlyList<WorkflowUser>>(root, s => s.SearchUsersAsync(query, limit, ct));
}

internal sealed class ScopedNotifications<T>(IServiceProvider root) : INotificationSender where T : class, INotificationSender
{
    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default) =>
        WorkflowServiceScope.UseAsync<T, bool>(root, async s => { await s.SendAsync(notification, ct); return true; });
}

internal sealed class ScopedSecrets<T>(IServiceProvider root) : IWorkflowSecretResolver where T : class, IWorkflowSecretResolver
{
    public Task<string?> GetSecretAsync(string name, CancellationToken ct = default) => WorkflowServiceScope.UseAsync<T, string?>(root, s => s.GetSecretAsync(name, ct));
}

internal sealed class NoIdentityService : IWorkflowIdentity
{
    public Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default) => Task.FromResult<WorkflowUser?>(new WorkflowUser(id, id));
    public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<string?> GetManagerAsync(string userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkflowUser>>([]);
}

internal sealed class NoNotificationService : INotificationSender
{
    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoSecretService : IWorkflowSecretResolver
{
    public Task<string?> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>Creates action handlers from the current DI scope.</summary>
internal sealed class ServiceProviderActionResolver(IServiceProvider root) : IWorkflowActionResolver
{
    public IWorkflowAction? Resolve(ActionDescriptor action)
    {
        if (action.HandlerType is null) return null;
        var sp = WorkflowServiceScope.Current ?? root;
        return (IWorkflowAction)(sp.GetService(action.HandlerType) ?? ActivatorUtilities.CreateInstance(sp, action.HandlerType));
    }
}
