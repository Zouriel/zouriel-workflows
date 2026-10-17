using System.Collections.Concurrent;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Testing;

/// <summary>
/// An engine wired to an in-memory store, a manual clock and recording fakes. Use it to test
/// workflows and actions without a database:
/// <code>
/// var host = new WorkflowTestHost(registry);
/// await host.Store.CreateRecordAsync("application", "a1", new() { ["status"] = "draft" });
/// await host.SettleAsync();
/// </code>
/// </summary>
public sealed class WorkflowTestHost
{
    public InMemoryWorkflowStore Store { get; }
    public ManualTimeProvider Clock { get; }
    public FakeIdentity Identity { get; } = new();
    public RecordingNotifications Notifications { get; } = new();
    public FakeWebhooks Webhooks { get; } = new();
    public FakeActions Actions { get; } = new();
    public WorkflowEngine Engine { get; }

    public WorkflowTestHost(WorkflowRegistry registry, DateTimeOffset? now = null, Func<WorkflowEngineOptions, WorkflowEngineOptions>? configure = null)
    {
        Store = new InMemoryWorkflowStore { Registry = registry };
        Clock = new ManualTimeProvider(now ?? new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
        var options = new WorkflowEngineOptions
        {
            Registry = registry, Store = Store, Identity = Identity, Notifications = Notifications, Webhooks = Webhooks, Actions = Actions, Time = Clock,
            RecordLinkTemplate = "https://app.test/{entity}/{id}",
        };
        Engine = new WorkflowEngine(configure?.Invoke(options) ?? options);
        Store.Changes = Engine.Changes;
    }

    /// <summary>Runs the background work (outbox, notifications, due timers) until nothing is left, like the hosted workers do.</summary>
    public async Task SettleAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 100; i++)
        {
            var n = await Engine.ProcessOutboxAsync(500, ct);
            n += await Engine.ProcessTimersAsync(500, ct);
            // Background failures are engine or test bugs; surface them instead of leaving them parked.
            var failed = Store.All<OutboxRecord>("outbox").FirstOrDefault(o => o.Error is not null);
            if (failed is not null) throw new InvalidOperationException($"Outbox {failed.Kind} failed: {failed.Error}");
            var timer = Store.All<AuditRecord>("audit").FirstOrDefault(a => a.Action == "timerFailed");
            if (timer is not null) throw new InvalidOperationException($"Timer failed: {timer.Data}");
            if (n == 0) return;
        }
        throw new InvalidOperationException("Background work did not settle after 100 rounds (a trigger loop?).");
    }

    /// <summary>Moves the clock forward in steps, firing timers as they come due, then settles.</summary>
    public async Task AdvanceAsync(TimeSpan by, CancellationToken ct = default)
    {
        var end = Clock.GetUtcNow() + by;
        while (true)
        {
            await SettleAsync(ct);
            var next = Store.All<BookmarkRecord>("bookmarks").Where(b => b.DueAt is not null && b.DueAt > Clock.GetUtcNow() && b.DueAt <= end)
                .Select(b => b.DueAt!.Value).DefaultIfEmpty(end).Min();
            Clock.Set(next);
            await SettleAsync(ct);
            if (next >= end) return;
        }
    }

    /// <summary>Creates and publishes a definition in one go. Returns its id.</summary>
    public async Task<string> PublishAsync(string json, CancellationToken ct = default)
    {
        var admin = WorkflowActor.User("admin", "Admin");
        var def = Model.Definitions.Parse(json);
        var existing = (await Engine.ListDefinitionsAsync(ct)).FirstOrDefault(d => d.Id == def.Id);
        if (existing is null) await Engine.CreateDefinitionAsync(json, admin, ct);
        else await Engine.SaveDraftAsync(def.Id, json, admin, ct);
        try
        {
            await Engine.PublishAsync(def.Id, null, admin, ct);
        }
        catch (WorkflowException e) when (e.Details is IEnumerable<Lint.LintProblem> problems)
        {
            throw new InvalidOperationException($"'{def.Key}' cannot be published: {string.Join(" ", problems.Select(p => $"[{p.Code}] {p.Message}"))}");
        }
        return def.Id;
    }
}

public sealed class FakeIdentity : IWorkflowIdentity
{
    private readonly ConcurrentDictionary<string, (WorkflowUser User, string? Manager)> _users = new();

    public void Add(WorkflowUser user, string? manager = null) => _users[user.Id] = (user, manager);

    public Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default) => Task.FromResult(_users.TryGetValue(id, out var u) ? u.User : null);

    public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_users.Values.Where(u => u.User.Roles?.Contains(role) == true).Select(u => u.User.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_users.Values.Where(u => u.User.Groups?.Contains(group) == true).Select(u => u.User.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());

    public Task<string?> GetManagerAsync(string userId, CancellationToken ct = default) => Task.FromResult(_users.TryGetValue(userId, out var u) ? u.Manager : null);

    public Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowUser>>(_users.Values.Select(u => u.User)
            .Where(u => string.IsNullOrEmpty(query) || u.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || u.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.Name, StringComparer.Ordinal).Take(limit).ToList());
}

public sealed class RecordingNotifications : INotificationSender
{
    private readonly ConcurrentQueue<WorkflowNotification> _sent = new();
    private readonly ConcurrentDictionary<string, bool> _keys = new();

    public IReadOnlyList<WorkflowNotification> Sent => _sent.ToList();

    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default)
    {
        // Senders should be idempotent on the key; this one is, so retried deliveries don't double up.
        if (_keys.TryAdd(notification.IdempotencyKey, true)) _sent.Enqueue(notification);
        return Task.CompletedTask;
    }
}

public sealed class FakeWebhooks : IWebhookClient
{
    private readonly ConcurrentQueue<WebhookRequest> _calls = new();

    public IReadOnlyList<WebhookRequest> Calls => _calls.ToList();

    /// <summary>Responses per URL prefix; unmatched calls answer 200 with an empty JSON object.</summary>
    public Dictionary<string, Queue<WebhookResponse>> Responses { get; } = [];

    public Task<WebhookResponse> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        _calls.Enqueue(request);
        foreach (var (prefix, q) in Responses)
            if (request.Url.StartsWith(prefix, StringComparison.Ordinal) && q.Count > 0) return Task.FromResult(q.Count > 1 ? q.Dequeue() : q.Peek());
        return Task.FromResult(new WebhookResponse(200, "{}"));
    }
}

/// <summary>Action handlers for tests: each key returns fixed outputs, optionally failing the first N calls.</summary>
public sealed class FakeActions : IWorkflowActionResolver
{
    private readonly ConcurrentDictionary<string, Behaviour> _behaviours = new();
    private readonly ConcurrentQueue<WorkflowActionContext> _calls = new();

    public IReadOnlyList<WorkflowActionContext> Calls => _calls.ToList();

    public void Set(string key, IReadOnlyDictionary<string, object?>? output = null, int failTimes = 0, string? failMessage = null) =>
        _behaviours[key] = new Behaviour(output ?? new Dictionary<string, object?>(), failTimes, failMessage ?? "The action failed.");

    public IWorkflowAction? Resolve(ActionDescriptor action) => new Handler(this, action.Key);

    private sealed record Behaviour(IReadOnlyDictionary<string, object?> Output, int FailTimes, string FailMessage)
    {
        public int Calls;
    }

    private sealed class Handler(FakeActions owner, string key) : IWorkflowAction
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
        {
            owner._calls.Enqueue(context);
            var b = owner._behaviours.GetOrAdd(key, _ => new Behaviour(new Dictionary<string, object?>(), 0, ""));
            b.Calls++;
            if (b.Calls <= b.FailTimes) throw new InvalidOperationException(b.FailMessage);
            return Task.FromResult(b.Output);
        }
    }
}
