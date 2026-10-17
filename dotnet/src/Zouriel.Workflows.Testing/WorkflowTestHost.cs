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
    /// <summary>The in-memory store; create, change and delete records through it as the app would.</summary>
    public InMemoryWorkflowStore Store { get; }
    /// <summary>The manual clock the engine and store use; move it with <see cref="AdvanceAsync"/>.</summary>
    public ManualTimeProvider Clock { get; }
    /// <summary>The people workflows see; add users with roles, groups and managers.</summary>
    public FakeIdentity Identity { get; } = new();
    /// <summary>Notifications delivered from the outbox (deduplicated by idempotency key).</summary>
    public RecordingNotifications Notifications { get; } = new();
    /// <summary>Webhook calls made, with canned responses per URL prefix.</summary>
    public FakeWebhooks Webhooks { get; } = new();
    /// <summary>Action handlers with fixed outputs, failures and delays.</summary>
    public FakeActions Actions { get; } = new();
    /// <summary>The engine under test.</summary>
    public WorkflowEngine Engine { get; }

    /// <summary>Creates a host. <paramref name="now"/> sets the clock (default 2026-10-19 09:00 UTC); <paramref name="configure"/> may replace the engine options.</summary>
    public WorkflowTestHost(WorkflowRegistry registry, DateTimeOffset? now = null, Func<WorkflowEngineOptions, WorkflowEngineOptions>? configure = null)
    {
        Store = new InMemoryWorkflowStore { Registry = registry };
        Clock = new ManualTimeProvider(now ?? new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
        Store.Time = Clock;
        var options = new WorkflowEngineOptions
        {
            Registry = registry, Store = Store, Identity = Identity, Notifications = Notifications, Webhooks = Webhooks, Actions = Actions, Time = Clock,
            RecordLinkTemplate = "https://app.test/{entity}/{id}",
        };
        Engine = new WorkflowEngine(configure?.Invoke(options) ?? options);
        Store.Changes = Engine.Changes;
    }

    /// <summary>When true, settling tolerates timers that fail (tests of backoff and parking); otherwise a failed timer fails the test.</summary>
    public bool AllowTimerFailures { get; set; }

    /// <summary>Runs the background work (outbox, notifications, due timers) until nothing is left, like the hosted workers do.</summary>
    public async Task SettleAsync(CancellationToken ct = default)
    {
        await Engine.FlushBackgroundAsync();
        for (var i = 0; i < 100; i++)
        {
            var n = await Engine.ProcessOutboxAsync(500, ct);
            n += await Engine.ProcessTimersAsync(500, ct);
            // Background failures are engine or test bugs; surface them instead of leaving them parked.
            var failed = Store.All<OutboxRecord>("outbox").FirstOrDefault(o => o.Error is not null);
            if (failed is not null) throw new InvalidOperationException($"Outbox {failed.Kind} failed: {failed.Error}");
            var timer = AllowTimerFailures ? null : Store.All<AuditRecord>("audit").FirstOrDefault(a => a.Action == "timerFailed");
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
            // A timer backing off after a failure comes due again when its lock runs out (parked ones never do).
            var next = Store.All<BookmarkRecord>("bookmarks").Where(b => b.DueAt is not null)
                .Select(b => b.LockedUntil is { } l && l > b.DueAt!.Value ? l : b.DueAt!.Value)
                .Where(at => at > Clock.GetUtcNow() && at <= end).DefaultIfEmpty(end).Min();
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

/// <summary>People for tests: users with roles, groups and a manager, added by hand.</summary>
public sealed class FakeIdentity : IWorkflowIdentity
{
    private readonly ConcurrentDictionary<string, (WorkflowUser User, string? Manager)> _users = new();

    /// <summary>User ids whose lookup throws, to test how the engine copes with a failing identity service.</summary>
    public ConcurrentDictionary<string, bool> Broken { get; } = new();

    /// <summary>Adds (or replaces) a user.</summary>
    public void Add(WorkflowUser user, string? manager = null) => _users[user.Id] = (user, manager);

    /// <inheritdoc />
    public Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default) =>
        Broken.ContainsKey(id) ? throw new InvalidOperationException($"The directory failed looking up '{id}'.") : Task.FromResult(_users.TryGetValue(id, out var u) ? u.User : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_users.Values.Where(u => u.User.Roles?.Contains(role) == true).Select(u => u.User.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_users.Values.Where(u => u.User.Groups?.Contains(group) == true).Select(u => u.User.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());

    /// <inheritdoc />
    public Task<string?> GetManagerAsync(string userId, CancellationToken ct = default) => Task.FromResult(_users.TryGetValue(userId, out var u) ? u.Manager : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowUser>>(_users.Values.Select(u => u.User)
            .Where(u => string.IsNullOrEmpty(query) || u.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || u.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.Name, StringComparer.Ordinal).Take(limit).ToList());
}

/// <summary>Records delivered notifications instead of sending them; idempotent on the notification key, as real senders should be.</summary>
public sealed class RecordingNotifications : INotificationSender
{
    private readonly ConcurrentQueue<WorkflowNotification> _sent = new();
    private readonly ConcurrentDictionary<string, bool> _keys = new();

    /// <summary>Everything delivered so far, in order.</summary>
    public IReadOnlyList<WorkflowNotification> Sent => _sent.ToList();

    /// <inheritdoc />
    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default)
    {
        // Senders should be idempotent on the key; this one is, so retried deliveries don't double up.
        if (_keys.TryAdd(notification.IdempotencyKey, true)) _sent.Enqueue(notification);
        return Task.CompletedTask;
    }
}

/// <summary>Records webhook calls and answers them from <see cref="Responses"/> (never touches the network).</summary>
public sealed class FakeWebhooks : IWebhookClient
{
    private readonly ConcurrentQueue<WebhookRequest> _calls = new();

    /// <summary>Every call made so far, in order.</summary>
    public IReadOnlyList<WebhookRequest> Calls => _calls.ToList();

    /// <summary>Responses per URL prefix; unmatched calls answer 200 with an empty JSON object.</summary>
    public Dictionary<string, Queue<WebhookResponse>> Responses { get; } = [];

    /// <summary>Records the call and returns the next queued response for its URL prefix (the last one repeats).</summary>
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
    private readonly ConcurrentQueue<WorkflowActionUndoContext> _undone = new();
    private readonly ConcurrentDictionary<string, bool> _undoable = new();

    /// <summary>Every action call made so far, in order (with idempotency keys and attempts).</summary>
    public IReadOnlyList<WorkflowActionContext> Calls => _calls.ToList();

    /// <summary>Every undo a reset asked for, in order.</summary>
    public IReadOnlyList<WorkflowActionUndoContext> Undone => _undone.ToList();

    /// <summary>Lets a reset undo an action (its handler then implements <see cref="IUndoableWorkflowAction"/>).</summary>
    public void Undoable(string key) => _undoable[key] = true;

    /// <summary>Sets what an action does: its outputs, how many first calls fail, and how long each call takes (honouring cancellation).</summary>
    public void Set(string key, IReadOnlyDictionary<string, object?>? output = null, int failTimes = 0, string? failMessage = null, TimeSpan? delay = null) =>
        _behaviours[key] = new Behaviour(output ?? new Dictionary<string, object?>(), failTimes, failMessage ?? "The action failed.", delay);

    /// <summary>A handler that behaves as <see cref="Set"/> said for the action's key (empty outputs when never set).</summary>
    public IWorkflowAction? Resolve(ActionDescriptor action) =>
        _undoable.ContainsKey(action.Key) ? new UndoableHandler(this, action.Key) : new Handler(this, action.Key);

    private sealed record Behaviour(IReadOnlyDictionary<string, object?> Output, int FailTimes, string FailMessage, TimeSpan? Delay = null)
    {
        public int Calls;
    }

    private sealed class UndoableHandler(FakeActions owner, string key) : IUndoableWorkflowAction
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct) =>
            new Handler(owner, key).ExecuteAsync(context, ct);

        public Task UndoAsync(WorkflowActionUndoContext context, CancellationToken ct)
        {
            owner._undone.Enqueue(context);
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(FakeActions owner, string key) : IWorkflowAction
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct)
        {
            owner._calls.Enqueue(context);
            var b = owner._behaviours.GetOrAdd(key, _ => new Behaviour(new Dictionary<string, object?>(), 0, ""));
            b.Calls++;
            if (b.Delay is { } delay) await Task.Delay(delay, ct);
            if (b.Calls <= b.FailTimes) throw new InvalidOperationException(b.FailMessage);
            return b.Output;
        }
    }
}
