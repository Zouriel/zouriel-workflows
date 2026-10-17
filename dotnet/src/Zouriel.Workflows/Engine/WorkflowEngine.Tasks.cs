using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;
using TaskStatus = Zouriel.Workflows.Storage.TaskStatus;

namespace Zouriel.Workflows.Engine;

/// <summary>One person's decision on a task.</summary>
/// <param name="UserId">Who decided.</param>
/// <param name="Name">Their name.</param>
/// <param name="Outcome">The outcome key they chose.</param>
/// <param name="Comment">Their comment, if any.</param>
/// <param name="Data">Their form answers, if the task has a form (masked in views).</param>
/// <param name="At">When (ISO 8601 UTC).</param>
public sealed record TaskDecision(string UserId, string? Name, string Outcome, string? Comment, Dictionary<string, object?>? Data, string At);

public sealed partial class WorkflowEngine
{
    private async Task CreateTaskAsync(Exec x, Token t, HumanStep h, CancellationToken ct)
    {
        var scope = x.Scope(t);
        var assignees = await ResolveAssigneesAsync(scope, h.Assignees, null, x.I.InitiatorId, ct);
        if (assignees.Count == 0) throw new WorkflowException("noAssignees", $"Nobody matches the people chosen for “{h.Title ?? h.Type}”.");
        var rec = await BeginStepAsync(x, t, h, new Dictionary<string, object?> { ["assignees"] = assignees }, ct);
        var due = Due(x, h.Due, h.Calendar);
        var title = h.Title ?? (h is ApprovalStep ? "Approval" : "Task");
        var instructions = h.Instructions is null ? null : await RenderAsync(scope, h.Instructions, ct);
        var outcomes = h switch
        {
            ApprovalStep => new List<object?>
            {
                new Dictionary<string, object?> { ["key"] = "approved", ["label"] = "Approve", ["tone"] = "success" },
                new Dictionary<string, object?> { ["key"] = "rejected", ["label"] = "Reject", ["tone"] = "danger" },
            },
            TaskStep tk => tk.Outcomes.Select(o => (object?)new Dictionary<string, object?> { ["key"] = o.Key, ["label"] = o.Label, ["tone"] = o.Tone }).ToList(),
            _ => [],
        };
        string? entityTitle = null;
        if (x.I.Entity is not null && x.I.EntityId is not null) entityTitle = await RecordTitleAsync(scope, x.I.Entity, x.I.EntityId, ct);
        var task = new TaskRecord
        {
            Id = NewId(), InstanceId = x.I.Id, RootInstanceId = x.I.Kind == InstanceKinds.Run ? x.I.ParentId : x.I.Id, NodeId = h.Id, TokenId = t.Id,
            Kind = h is ApprovalStep ? "approval" : "task", Title = title, Instructions = instructions, DefinitionKey = x.I.DefinitionKey,
            Entity = x.I.Entity, EntityId = x.I.EntityId, EntityTitle = entityTitle, Status = TaskStatus.Open, Assignees = Values.Serialize(assignees),
            Rule = h is ApprovalStep ap ? Values.Serialize(new Dictionary<string, object?> { ["type"] = ap.Rule.Type, ["value"] = ap.Rule.Value }) : null,
            Outcomes = Values.Serialize(outcomes), Form = h.Form?.ToJsonString(), RequireComment = h.RequireComment,
            AllowDelegation = h.AllowDelegation ?? x.U.Settings.People?.Delegation ?? false, DueAt = due, CreatedAt = x.U.Now,
        };
        await x.U.S.AddTaskAsync(task, ct);
        await AddTimerAsync(x, t, h, BookmarkPurposes.Due, due, null, ct, task.Id);
        var reminders = h.Reminders ?? x.U.Settings.Defaults?.Reminders ?? [];
        for (var i = 0; i < reminders.Count; i++)
            if (IsoDuration.TryParse(reminders[i], out var r)) await AddTimerAsync(x, t, h, BookmarkPurposes.Reminder, x.U.Calendar.Add(x.U.Now, r, IsWorking(x, h.Calendar)), i.ToString(System.Globalization.CultureInfo.InvariantCulture), ct, task.Id);
        if (h.Escalation is { } esc && IsoDuration.TryParse(esc.After, out var ea))
            await AddTimerAsync(x, t, h, BookmarkPurposes.Escalation, x.U.Calendar.Add(x.U.Now, ea, IsWorking(x, h.Calendar)), null, ct, task.Id);
        Wait(t, $"task:{task.Id}", rec);
        await x.U.S.UpdateStepAsync(rec, ct);
        await x.U.AuditAsync(TaskAudit(x.U, task, "created", x.Actor, new Dictionary<string, object?>
        {
            ["title"] = title, ["assignees"] = assignees, ["due"] = Values.FormatInstant(due), ["step"] = h.Id,
        }), ct);
        x.U.Log?.Add("task", "created", task.Id, title, new Dictionary<string, object?> { ["assignees"] = assignees, ["outcomes"] = outcomes });
        await NotifyAssigneesAsync(x.U, task, assignees, $"New {task.Kind}: {title}", instructions ?? $"“{title}” is waiting for you.", "created", ct);
    }

    private async Task NotifyAssigneesAsync(Uow u, TaskRecord task, IReadOnlyList<string> userIds, string subject, string body, string why, CancellationToken ct)
    {
        if (!Options.NotifyTaskAssignees || userIds.Count == 0) return;
        var channel = Options.TaskNotificationChannel ?? Registry.Channels.FirstOrDefault(c => c.Key == "inApp")?.Key ?? Registry.Channels.FirstOrDefault(c => c.Key == "email")?.Key;
        if (channel is null || u.Settings.Channels?.GetValueOrDefault(channel)?.Enabled == false) return;
        var to = new List<NotificationRecipient>();
        foreach (var id in userIds)
        {
            var user = await Options.Identity.GetUserAsync(id, ct);
            to.Add(new NotificationRecipient(id, user?.Email, user?.Name ?? id));
        }
        var subjectWithRecord = task.EntityTitle is null ? subject : $"{subject} ({task.EntityTitle})";
        await QueueNotificationAsync(u, new WorkflowNotification(channel, to, subjectWithRecord, body, RecordLink(task.Entity, task.EntityId), task.InstanceId,
            task.Entity, task.EntityId, $"task:{task.Id}:{why}:{task.EscalationLevel}"), ct);
    }

    // ── Decisions ─────────────────────────────────────────────────────────

    /// <summary>Records a person's decision on an open task, and continues the workflow when the task is settled.</summary>
    public Task<TaskView> DecideAsync(string taskId, WorkflowActor user, string outcome, string? comment, Dictionary<string, object?>? data, CancellationToken ct = default)
    {
        if (!user.IsUser) throw new WorkflowException("notAllowed", "Only a person can decide a task.");
        return RunAsync(async u =>
        {
            await DecideCoreAsync(u, taskId, user, outcome, comment, data, ct);
            return await TaskViewAsync(u, (await u.S.GetTaskAsync(taskId, ct))!, user, ct);
        }, ct);
    }

    internal async Task DecideCoreAsync(Uow u, string taskId, WorkflowActor user, string outcome, string? comment, Dictionary<string, object?>? data, CancellationToken ct)
    {
        var task = await u.S.GetTaskAsync(taskId, ct) ?? throw new WorkflowException("notFound", "That task does not exist.");
        if (task.Status != TaskStatus.Open) throw new WorkflowException("taskClosed", "This task has already been completed.");
        if (!user.IsUser) throw new WorkflowException("notAllowed", "Only a person can decide a task.");
        var assignees = Values.FromJson(task.Assignees) as List<object?> ?? [];
        if (!assignees.Any(a => Values.Text(a) == user.Id)) throw new WorkflowException("notAllowed", "This task is not assigned to you.");
        var decisions = ParseDecisions(task);
        if (decisions.Any(d => d.UserId == user.Id)) throw new WorkflowException("alreadyDecided", "You have already decided this task.");
        var outcomes = (Values.FromJson(task.Outcomes) as List<object?> ?? []).OfType<Dictionary<string, object?>>().Select(o => o["key"] as string).ToList();
        if (!outcomes.Contains(outcome)) throw new WorkflowException("invalidOutcome", $"'{outcome}' is not one of this task's choices.");
        var needsComment = task.RequireComment == "always" || (task.RequireComment == "reject" && outcome == "rejected");
        if (needsComment && string.IsNullOrWhiteSpace(comment)) throw new WorkflowException("commentRequired", "Add a comment to explain your decision.");
        data ??= [];
        if (Values.Serialize(data).Length > MaxDecisionDataBytes) throw new WorkflowException("tooLarge", "The answers are too large.");
        if (task.Form is not null)
        {
            var errors = Options.FormValidator.Validate(JsonNode.Parse(task.Form)!.AsObject(), data, u.Today);
            if (errors.Count > 0) throw new WorkflowException("invalidInput", "Some answers need attention.", errors);
        }
        var decision = new TaskDecision(user.Id!, user.Name, outcome, comment, data, Values.FormatInstant(u.Now));
        decisions.Add(decision);
        task.Decisions = Values.Serialize(decisions.Select(DecisionValue).ToList());
        await u.AuditAsync(TaskAudit(u, task, "decided", user, new Dictionary<string, object?> { ["outcome"] = outcome, ["comment"] = comment, ["answers"] = data }), ct);
        u.Log?.Add("task", "decided", task.Id, $"{user.Name ?? user.Id}: {outcome}");

        await SaveAndSettleAsync(u, task, decisions, assignees.Count, ct);
    }

    /// <summary>Saves a task and, when its decisions now settle it (spec §7 rules), completes it and continues the workflow.</summary>
    private async Task SaveAndSettleAsync(Uow u, TaskRecord task, List<TaskDecision> decisions, int assigneeCount, CancellationToken ct)
    {
        var settled = Settle(task, decisions, assigneeCount);
        if (settled is null)
        {
            await u.S.UpdateTaskAsync(task, ct);
            return;
        }
        task.Status = TaskStatus.Completed;
        task.Outcome = settled;
        task.CompletedAt = u.Now;
        await u.S.UpdateTaskAsync(task, ct);
        await ContinueAfterTaskAsync(u, task, settled, decisions, ct);
    }

    /// <summary>The largest decision answers accepted, as JSON (256 KB).</summary>
    public const int MaxDecisionDataBytes = 256 * 1024;

    private static Dictionary<string, object?> DecisionValue(TaskDecision d) => new()
    {
        ["userId"] = d.UserId, ["name"] = d.Name, ["outcome"] = d.Outcome, ["comment"] = d.Comment, ["data"] = d.Data, ["at"] = d.At,
    };

    internal static List<TaskDecision> ParseDecisions(TaskRecord task) =>
        (Values.FromJson(task.Decisions) as List<object?> ?? []).OfType<Dictionary<string, object?>>().Select(d => new TaskDecision(
            Values.Text(d.GetValueOrDefault("userId")), d.GetValueOrDefault("name") as string, Values.Text(d.GetValueOrDefault("outcome")),
            d.GetValueOrDefault("comment") as string, d.GetValueOrDefault("data") as Dictionary<string, object?>, Values.Text(d.GetValueOrDefault("at")))).ToList();

    /// <summary>The settled outcome of a task, or null while more decisions are needed (spec §7 assignee rules).</summary>
    internal static string? Settle(TaskRecord task, List<TaskDecision> decisions, int assigneeCount)
    {
        if (task.Kind != "approval") return decisions.Count > 0 ? decisions[^1].Outcome : null;
        var rule = Values.FromJson(task.Rule ?? "{}") as Dictionary<string, object?> ?? [];
        var type = rule.GetValueOrDefault("type") as string ?? "any";
        var value = rule.GetValueOrDefault("value") is double v ? (int)v : 1;
        var approvals = decisions.Count(d => d.Outcome == "approved");
        var rejections = decisions.Count(d => d.Outcome == "rejected");
        var needed = type switch
        {
            "all" => assigneeCount,
            "count" => Math.Min(value, assigneeCount),
            "percent" => (int)Math.Ceiling(assigneeCount * Math.Clamp(value, 1, 100) / 100.0),
            _ => 1,
        };
        if (type == "all" && rejections > 0) return "rejected";
        if (type == "any" && approvals > 0) return "approved";
        if (type == "any") return rejections >= assigneeCount ? "rejected" : null;
        if (approvals >= needed) return "approved";
        // Rejected once the remaining people can no longer reach the number needed.
        var undecided = assigneeCount - decisions.Count;
        return approvals + undecided < needed ? "rejected" : null;
    }

    private async Task ContinueAfterTaskAsync(Uow u, TaskRecord task, string outcome, List<TaskDecision> decisions, CancellationToken ct)
    {
        var x = await LoadExecAsync(u, task.InstanceId, ct);
        var t = x.S.Token(task.TokenId);
        var step = x.C.Step(task.NodeId) as HumanStep;
        if (t is null || step is null || t.Status != TokenStatus.Waiting) return;
        // Remove the task's timers (due, reminders, escalation).
        foreach (var b in (await u.S.ListBookmarksAsync(x.I.Id, ct)).Where(b => b.TaskId == task.Id)) await u.S.RemoveBookmarkAsync(b.Id, ct);
        x.S.SetOutput(step.Id, "outcome", outcome);
        x.S.SetOutput(step.Id, "decidedBy", decisions.Select(d => (object?)d.UserId).ToList());
        if (step is ApprovalStep) x.S.SetOutput(step.Id, "comments", decisions.Where(d => d.Comment is not null).Select(d => (object?)$"{d.Name ?? d.UserId}: {d.Comment}").ToList());
        else x.S.SetOutput(step.Id, "comment", decisions.LastOrDefault()?.Comment);
        x.S.SetOutput(step.Id, "data", decisions.LastOrDefault()?.Data);
        Resume(t);
        await CompleteWaitingStepAsync(x, t, step, ct);
        t.Frames.Add(new Frame { List = step is ApprovalStep ? $"{step.Id}/outcome:{outcome}" : outcome == "timedOut" ? $"{step.Id}/onTimeout" : $"{step.Id}/outcome:{outcome}" });
        await DriveAsync(x, ct);
    }

    /// <summary>A task's due time passed before it was settled.</summary>
    private async Task TaskTimedOutAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        var task = b.TaskId is null ? null : await u.S.GetTaskAsync(b.TaskId, ct);
        if (task is null || task.Status != TaskStatus.Open) return;
        task.Status = TaskStatus.TimedOut;
        task.Outcome = "timedOut";
        task.CompletedAt = u.Now;
        await u.S.UpdateTaskAsync(task, ct);
        await u.AuditAsync(TaskAudit(u, task, "timedOut", WorkflowActor.System), ct);
        u.Log?.Add("task", "timedOut", task.Id, task.Title);
        await ContinueAfterTaskAsync(u, task, "timedOut", ParseDecisions(task), ct);
    }

    private async Task TaskReminderAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        var task = b.TaskId is null ? null : await u.S.GetTaskAsync(b.TaskId, ct);
        if (task is null || task.Status != TaskStatus.Open) return;
        var decided = ParseDecisions(task).Select(d => d.UserId).ToHashSet();
        var waitingOn = (Values.FromJson(task.Assignees) as List<object?> ?? []).Select(Values.Text).Where(id => !decided.Contains(id)).ToList();
        await u.AuditAsync(TaskAudit(u, task, "reminded", WorkflowActor.System, new Dictionary<string, object?> { ["to"] = waitingOn }), ct);
        u.Log?.Add("task", "reminded", task.Id, task.Title);
        await NotifyAssigneesAsync(u, task, waitingOn, $"Reminder: {task.Title}", $"“{task.Title}” is still waiting for you. It is due {Values.FormatInstant(task.DueAt)}.", $"reminder{b.Payload}", ct);
    }

    private async Task TaskEscalationAsync(Uow u, BookmarkRecord b, CancellationToken ct)
    {
        var task = b.TaskId is null ? null : await u.S.GetTaskAsync(b.TaskId, ct);
        if (task is null || task.Status != TaskStatus.Open) return;
        var x = await LoadExecAsync(u, task.InstanceId, ct);
        if (x.C.Step(task.NodeId) is not HumanStep { Escalation: { } esc } step) return;
        var current = (Values.FromJson(task.Assignees) as List<object?> ?? []).Select(Values.Text).ToList();
        var added = await ResolveAssigneesAsync(x.Scope(x.S.Token(task.TokenId)), esc.Assignees, current, x.I.InitiatorId, ct);
        var fresh = added.Where(a => !current.Contains(a)).ToList();
        task.Assignees = Values.Serialize(current.Concat(fresh).ToList());
        task.EscalationLevel++;
        await u.S.UpdateTaskAsync(task, ct);
        await u.AuditAsync(TaskAudit(u, task, "escalated", WorkflowActor.System, new Dictionary<string, object?> { ["added"] = fresh, ["step"] = step.Id }), ct);
        u.Log?.Add("task", "escalated", task.Id, task.Title);
        if (esc.Notify != false) await NotifyAssigneesAsync(u, task, fresh, $"Escalated to you: {task.Title}", $"“{task.Title}” is overdue and has been passed to you.", "escalated", ct);
    }

    /// <summary>Hands an open task from one assignee to someone else (when the task allows delegation).</summary>
    public Task<TaskView> DelegateAsync(string taskId, WorkflowActor user, string toUserId, string? note, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var task = await u.S.GetTaskAsync(taskId, ct) ?? throw new WorkflowException("notFound", "That task does not exist.");
            if (task.Status != TaskStatus.Open) throw new WorkflowException("taskClosed", "This task has already been completed.");
            if (!task.AllowDelegation) throw new WorkflowException("notAllowed", "This task cannot be handed to someone else.");
            var list = (Values.FromJson(task.Assignees) as List<object?> ?? []).Select(Values.Text).ToList();
            if (!list.Contains(user.Id!)) throw new WorkflowException("notAllowed", "This task is not assigned to you.");
            var decisions = ParseDecisions(task);
            if (decisions.Any(d => d.UserId == user.Id)) throw new WorkflowException("alreadyDecided", "You have already decided this task.");
            // Handing it to someone already on it (or who already decided) would quietly lower what the rule needs.
            if (list.Contains(toUserId) || decisions.Any(d => d.UserId == toUserId))
                throw new WorkflowException("alreadyAssigned", "That person is already on this task.");
            if (await Options.Identity.GetUserAsync(toUserId, ct) is null) throw new WorkflowException("unknownUser", "That person does not exist.");
            list[list.IndexOf(user.Id!)] = toUserId;
            task.Assignees = Values.Serialize(list);
            await u.AuditAsync(TaskAudit(u, task, "delegated", user, new Dictionary<string, object?> { ["from"] = user.Id, ["to"] = toUserId, ["note"] = note }), ct);
            await SaveAndSettleAsync(u, task, decisions, list.Count, ct);
            await NotifyAssigneesAsync(u, task, [toUserId], $"Handed to you: {task.Title}", note ?? $"{user.Name ?? user.Id} passed “{task.Title}” to you.", $"delegated:{user.Id}", ct);
            return await TaskViewAsync(u, task, user, ct);
        }, ct);

    /// <summary>An admin replaces the assignees of an open task.</summary>
    public Task<TaskView> ReassignAsync(string taskId, WorkflowActor admin, IReadOnlyList<string> userIds, string? reason, CancellationToken ct = default) =>
        RunAsync(async u =>
        {
            var task = await u.S.GetTaskAsync(taskId, ct) ?? throw new WorkflowException("notFound", "That task does not exist.");
            if (task.Status != TaskStatus.Open) throw new WorkflowException("taskClosed", "This task has already been completed.");
            if (userIds.Count == 0) throw new WorkflowException("noAssignees", "Choose at least one person.");
            foreach (var id in userIds)
                if (await Options.Identity.GetUserAsync(id, ct) is null) throw new WorkflowException("unknownUser", $"'{id}' is not a person the app knows.");
            var before = Values.FromJson(task.Assignees);
            var decisions = ParseDecisions(task);
            var assignees = userIds.Concat(decisions.Select(d => d.UserId)).Distinct().ToList();
            task.Assignees = Values.Serialize(assignees);
            await u.AuditAsync(TaskAudit(u, task, "reassigned", admin, new Dictionary<string, object?> { ["reason"] = reason }) with
            {
                Before = before, After = Values.FromJson(task.Assignees),
            }, ct);
            await SaveAndSettleAsync(u, task, decisions, assignees.Count, ct);
            await NotifyAssigneesAsync(u, task, userIds, $"Assigned to you: {task.Title}", reason ?? $"“{task.Title}” has been assigned to you.", $"reassigned:{u.Now.ToUnixTimeMilliseconds()}", ct);
            return await TaskViewAsync(u, task, admin, ct);
        }, ct);
}
