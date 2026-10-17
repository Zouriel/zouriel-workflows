using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Zouriel.Workflows.Sample;

/// <summary>The people of the demo office. A real app reads these from its user store.</summary>
public static class DemoUsers
{
    /// <summary>Every demo person, their manager and job title.</summary>
    public static readonly IReadOnlyList<(WorkflowUser User, string? Manager, string Title)> All =
    [
        (new WorkflowUser("ali", "Ali Hassan", "ali@example.com", []), null, "Applicant"),
        (new WorkflowUser("sara", "Sara Ahmed", "sara@example.com", []), null, "Applicant"),
        (new WorkflowUser("rana", "Rana Ibrahim", "rana@example.com", ["reviewer"], ["panel"]), "hana", "Reviewer"),
        (new WorkflowUser("omar", "Omar Saleem", "omar@example.com", ["reviewer"], ["panel"]), "hana", "Reviewer"),
        (new WorkflowUser("farah", "Farah Nasser", "farah@example.com", ["finance"]), "hana", "Finance officer"),
        (new WorkflowUser("hana", "Hana Yusuf", "hana@example.com", ["head", "admin"]), null, "Head of office"),
    ];

    /// <summary>The demo person with this id, if any.</summary>
    public static WorkflowUser? Find(string? id) => All.FirstOrDefault(u => u.User.Id == id).User;
}

/// <summary>The demo people as the workflow engine's identity source.</summary>
public sealed class DemoIdentity : IWorkflowIdentity
{
    public Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default) => Task.FromResult(DemoUsers.Find(id));

    public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(DemoUsers.All.Where(u => u.User.Roles?.Contains(role) == true).Select(u => u.User.Id).ToList());

    public Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(DemoUsers.All.Where(u => u.User.Groups?.Contains(group) == true).Select(u => u.User.Id).ToList());

    public Task<string?> GetManagerAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(DemoUsers.All.FirstOrDefault(u => u.User.Id == userId).Manager);

    public Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowUser>>(DemoUsers.All.Select(u => u.User)
            .Where(u => string.IsNullOrWhiteSpace(query) || u.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || u.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList());
}

/// <summary>
/// The demo's bank: a switch the playground flips to show a workflow that stops because a system it
/// calls is down (Create invoice fails while it is), and how people put it right from the panel.
/// </summary>
public sealed class DemoBank
{
    /// <summary>Whether the bank refuses calls.</summary>
    public bool Down { get; set; }
}

/// <summary>Keeps sent notifications so the demo can show each person's inbox. A real app sends email/push here.</summary>
public sealed class DemoInbox : INotificationSender
{
    private readonly ConcurrentQueue<(WorkflowNotification Message, DateTimeOffset At)> _sent = new();
    private readonly ConcurrentDictionary<string, bool> _seen = new();

    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default)
    {
        if (!_seen.TryAdd(notification.IdempotencyKey, true)) return Task.CompletedTask;
        _sent.Enqueue((notification, DateTimeOffset.UtcNow));
        while (_sent.Count > 500 && _sent.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }

    public IReadOnlyList<(WorkflowNotification Message, DateTimeOffset At)> For(string? userId) =>
        _sent.Where(n => userId is null || n.Message.To.Any(r => r.UserId == userId || r.Email == DemoUsers.Find(userId)?.Email)).Reverse().ToList();
}

/// <summary>Real time plus an offset the demo can move forward, so waits and deadlines can be shown in seconds.</summary>
public sealed class OffsetTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}

/// <summary>
/// Demo sign-in: the <c>X-Demo-User</c> header picks one of <see cref="DemoUsers"/>. Replace with your
/// real authentication; the workflow API only needs a <see cref="ClaimsPrincipal"/>.
/// </summary>
public sealed class DemoAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The authentication scheme name.</summary>
    public const string SchemeName = "Demo";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Header only: an identity in the query string would end up in logs, history and Referer headers.
        var id = Request.Headers["X-Demo-User"].FirstOrDefault();
        var user = DemoUsers.Find(id);
        if (user is null) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user.Id), new("name", user.Name) };
        claims.AddRange((user.Roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
