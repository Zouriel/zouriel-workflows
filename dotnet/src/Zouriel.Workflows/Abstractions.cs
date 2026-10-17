namespace Zouriel.Workflows;

/// <summary>A refusal with a stable code the API returns (e.g. <c>notAllowed</c>, <c>guardFailed</c>).</summary>
public class WorkflowException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

/// <summary>Raised from the change hook when code writes a field a lifecycle owns (enforce mode).</summary>
public sealed class WorkflowFieldOwnedException(string entity, string field, string lifecycle)
    : WorkflowException("fieldOwned", $"'{entity}.{field}' is controlled by the '{lifecycle}' workflow; change it by applying a transition.")
{
    public string Entity { get; } = entity;
    public string Field { get; } = field;
    public string Lifecycle { get; } = lifecycle;
}

/// <summary>Who did something.</summary>
public sealed record WorkflowActor(string Type, string? Id = null, string? Name = null, IReadOnlyList<string>? Roles = null)
{
    public static readonly WorkflowActor System = new("system", null, "System");

    public static WorkflowActor User(string id, string? name = null, IReadOnlyList<string>? roles = null) => new("user", id, name, roles);

    public static WorkflowActor Workflow(string definitionKey, string instanceId) => new("workflow", instanceId, definitionKey);

    public bool IsUser => Type == "user" && Id is not null;
}

public sealed record WorkflowUser(string Id, string Name, string? Email = null, IReadOnlyList<string>? Roles = null, IReadOnlyList<string>? Groups = null);

/// <summary>Where users, roles, groups and managers come from. Implemented by the host app.</summary>
public interface IWorkflowIdentity
{
    Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default);
    Task<string?> GetManagerAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default);
}

public sealed record NotificationRecipient(string? UserId, string? Email, string? Name);

public sealed record WorkflowNotification(
    string Channel,
    IReadOnlyList<NotificationRecipient> To,
    string? Subject,
    string Body,
    string? Link,
    string InstanceId,
    string? Entity,
    string? EntityId,
    string IdempotencyKey);

/// <summary>Sends notifications. Implemented by the host app (email, in-app, SMS…).</summary>
public interface INotificationSender
{
    Task SendAsync(WorkflowNotification notification, CancellationToken ct = default);
}

public sealed record WebhookRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body, string IdempotencyKey, TimeSpan? Timeout);

public sealed record WebhookResponse(int Status, string? Body);

/// <summary>Calls webhooks. The default uses <see cref="HttpClient"/>.</summary>
public interface IWebhookClient
{
    Task<WebhookResponse> SendAsync(WebhookRequest request, CancellationToken ct = default);
}

/// <summary>Resolves named secrets for webhook connections. Secrets never live in settings.</summary>
public interface IWorkflowSecretResolver
{
    Task<string?> GetSecretAsync(string name, CancellationToken ct = default);
}

public sealed class WorkflowActionContext
{
    public required string ActionKey { get; init; }
    public required IReadOnlyDictionary<string, object?> Input { get; init; }
    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    public required string InstanceId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required int Attempt { get; init; }
    public WorkflowActor? Initiator { get; init; }
    /// <summary>True during a test run (<c>/simulate</c>): do not cause real side effects.</summary>
    public bool Simulation { get; init; }
}

/// <summary>An app-provided step (spec §7 <c>action</c>). Return the declared outputs.</summary>
public interface IWorkflowAction
{
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct);
}

/// <summary>Resolves action handlers (from DI in ASP.NET apps).</summary>
public interface IWorkflowActionResolver
{
    IWorkflowAction? Resolve(Registry.ActionDescriptor action);
}
