namespace Zouriel.Workflows;

/// <summary>A refusal with a stable code the API returns (e.g. <c>notAllowed</c>, <c>guardFailed</c>).</summary>
public class WorkflowException(string code, string message, object? details = null) : Exception(message)
{
    /// <summary>The stable refusal code (spec error codes).</summary>
    public string Code { get; } = code;
    /// <summary>Extra detail for the caller, e.g. lint problems or form errors.</summary>
    public object? Details { get; } = details;
}

/// <summary>Raised from the change hook when code writes a field a lifecycle owns (enforce mode).</summary>
public sealed class WorkflowFieldOwnedException(string entity, string field, string lifecycle)
    : WorkflowException("fieldOwned", $"'{entity}.{field}' is controlled by the '{lifecycle}' workflow; change it by applying a transition.")
{
    /// <summary>The entity whose field was written.</summary>
    public string Entity { get; } = entity;
    /// <summary>The lifecycle-owned field.</summary>
    public string Field { get; } = field;
    /// <summary>The key of the lifecycle that owns it.</summary>
    public string Lifecycle { get; } = lifecycle;
}

/// <summary>Who did something.</summary>
/// <param name="Type"><c>user</c>, <c>system</c> or <c>workflow</c> (anything else, e.g. <c>anonymous</c>, is not a person).</param>
/// <param name="Id">The user id, or the instance id for workflows; null for the system.</param>
/// <param name="Name">A display name.</param>
/// <param name="Roles">Roles the person carries from their sign-in; they count for <c>who</c> and manual starts.</param>
public sealed record WorkflowActor(string Type, string? Id = null, string? Name = null, IReadOnlyList<string>? Roles = null)
{
    /// <summary>The engine itself (timers, automatic transitions, app code).</summary>
    public static readonly WorkflowActor System = new("system", null, "System");

    /// <summary>A signed-in person.</summary>
    public static WorkflowActor User(string id, string? name = null, IReadOnlyList<string>? roles = null) => new("user", id, name, roles);

    /// <summary>A workflow instance acting on its own (its writes and steps).</summary>
    public static WorkflowActor Workflow(string definitionKey, string instanceId) => new("workflow", instanceId, definitionKey);

    /// <summary>True for a person with an id; only persons may apply transitions, decide tasks or start manual workflows.</summary>
    public bool IsUser => Type == "user" && Id is not null;
}

/// <summary>A person the app's directory knows.</summary>
/// <param name="Id">The user id assignees and actors use.</param>
/// <param name="Name">The display name templates render.</param>
/// <param name="Email">Where email notifications go, if any.</param>
/// <param name="Roles">Role keys the person has (registry roles).</param>
/// <param name="Groups">Group keys the person belongs to (registry groups).</param>
public sealed record WorkflowUser(string Id, string Name, string? Email = null, IReadOnlyList<string>? Roles = null, IReadOnlyList<string>? Groups = null);

/// <summary>Where users, roles, groups and managers come from. Implemented by the host app.</summary>
public interface IWorkflowIdentity
{
    /// <summary>A person by id, or null when unknown.</summary>
    Task<WorkflowUser?> GetUserAsync(string id, CancellationToken ct = default);
    /// <summary>The ids of everyone with a role.</summary>
    Task<IReadOnlyList<string>> GetUsersInRoleAsync(string role, CancellationToken ct = default);
    /// <summary>The ids of everyone in a group.</summary>
    Task<IReadOnlyList<string>> GetUsersInGroupAsync(string group, CancellationToken ct = default);
    /// <summary>A person's manager's id, or null (spec §8 <c>managerOf</c>).</summary>
    Task<string?> GetManagerAsync(string userId, CancellationToken ct = default);
    /// <summary>People whose name or id contains <paramref name="query"/> (for pickers), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<WorkflowUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct = default);
}

/// <summary>One recipient of a notification.</summary>
/// <param name="UserId">The person, when the recipient is a user (null for a bare email address).</param>
/// <param name="Email">Their email address, if known.</param>
/// <param name="Name">A display name.</param>
public sealed record NotificationRecipient(string? UserId, string? Email, string? Name);

/// <summary>A notification for the host to deliver (spec §7 <c>notify</c>, and task messages).</summary>
/// <param name="Channel">The registry channel key (<c>email</c>, <c>inApp</c>, …).</param>
/// <param name="To">The recipients, duplicates merged.</param>
/// <param name="Subject">The rendered subject, if the channel uses one.</param>
/// <param name="Body">The rendered message (with the channel footer from settings).</param>
/// <param name="Link">A link to the record, or a rendered link, if any.</param>
/// <param name="InstanceId">The instance that sent it.</param>
/// <param name="Entity">The record's entity, if any.</param>
/// <param name="EntityId">The record's id, if any.</param>
/// <param name="IdempotencyKey">Stable across redelivery; senders should drop repeats with the same key.</param>
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
    /// <summary>Delivers a notification; throwing makes the outbox retry it later.</summary>
    Task SendAsync(WorkflowNotification notification, CancellationToken ct = default);
}

/// <summary>One webhook call (spec §7 <c>webhook</c>).</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The full address, already checked against the allowed origins or connection.</param>
/// <param name="Headers">Headers to send (including a connection's secret header).</param>
/// <param name="Body">The JSON body, if any.</param>
/// <param name="IdempotencyKey">Sent as <c>Idempotency-Key</c>; stable for one attempt of one step.</param>
/// <param name="Timeout">How long the call may take, if limited.</param>
public sealed record WebhookRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body, string IdempotencyKey, TimeSpan? Timeout);

/// <summary>A webhook's answer.</summary>
/// <param name="Status">The HTTP status code (redirects are returned, not followed).</param>
/// <param name="Body">The response body as text, if any.</param>
public sealed record WebhookResponse(int Status, string? Body);

/// <summary>Calls webhooks. The default uses <see cref="HttpClient"/>.</summary>
public interface IWebhookClient
{
    /// <summary>Sends the request and returns the status and body; throws <c>HttpRequestException</c> on network failure.</summary>
    Task<WebhookResponse> SendAsync(WebhookRequest request, CancellationToken ct = default);
}

/// <summary>Resolves named secrets for webhook connections. Secrets never live in settings.</summary>
public interface IWorkflowSecretResolver
{
    /// <summary>The secret's value, or null when it is not set up.</summary>
    Task<string?> GetSecretAsync(string name, CancellationToken ct = default);
}

/// <summary>What an app action handler is given (spec §7 <c>action</c>).</summary>
public sealed class WorkflowActionContext
{
    /// <summary>The registry action key.</summary>
    public required string ActionKey { get; init; }
    /// <summary>The step's evaluated inputs, by input key.</summary>
    public required IReadOnlyDictionary<string, object?> Input { get; init; }
    /// <summary>The record's entity, if the workflow has one.</summary>
    public string? Entity { get; init; }
    /// <summary>The record's id.</summary>
    public string? EntityId { get; init; }
    /// <summary>The instance running the step.</summary>
    public required string InstanceId { get; init; }
    /// <summary>Stable for one attempt of one step; use it to make the action safe to repeat.</summary>
    public required string IdempotencyKey { get; init; }
    /// <summary>1-based attempt number (retries count up).</summary>
    public required int Attempt { get; init; }
    /// <summary>The person who started the instance, if any.</summary>
    public WorkflowActor? Initiator { get; init; }
    /// <summary>True during a test run (<c>/simulate</c>): do not cause real side effects.</summary>
    public bool Simulation { get; init; }
}

/// <summary>An app-provided step (spec §7 <c>action</c>). Return the declared outputs.</summary>
public interface IWorkflowAction
{
    /// <summary>Runs the action inside the unit of work; return the declared outputs. Throwing fails the attempt and undoes its record writes.</summary>
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(WorkflowActionContext context, CancellationToken ct);
}

/// <summary>Resolves action handlers (from DI in ASP.NET apps).</summary>
public interface IWorkflowActionResolver
{
    /// <summary>The handler for an action, or null when none is registered.</summary>
    IWorkflowAction? Resolve(Registry.ActionDescriptor action);
}
