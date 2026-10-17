using System.Globalization;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

public sealed partial class WorkflowEngine
{
    /// <summary>One instance being driven inside a unit of work.</summary>
    internal sealed class Exec(Uow u, InstanceRecord instance, CompiledDefinition def, RunState state)
    {
        public Uow U { get; } = u;
        public InstanceRecord I { get; } = instance;
        public CompiledDefinition C { get; } = def;
        public RunState S { get; } = state;
        public bool Ended { get; set; }
        /// <summary>The step record of the step being executed right now (so a thrown error can close it).</summary>
        public StepRecord? Current { get; set; }
        public WorkflowActor Actor => WorkflowActor.Workflow(I.DefinitionKey, I.Id);
        public ValueScope Scope(Token? t = null) => U.Scope(I, S, t);
    }

    // ── Starting ──────────────────────────────────────────────────────────

    internal async Task<InstanceRecord?> StartAutomationAsync(
        Uow u, CompiledDefinition c, string? entity, string? entityId, TriggerInfo trigger, WorkflowActor initiator, int depth,
        Dictionary<string, object?>? input, string? parentId, string? reason, string? correlationId, CancellationToken ct, bool drive = true,
        Dictionary<string, object?>? snapshot = null)
    {
        var def = c.Definition;
        var concurrency = def.Settings?.Concurrency ?? u.Settings.DefaultConcurrency;
        var queued = false;
        if (entityId is not null && concurrency != "parallel")
        {
            var busy = await u.S.CountInstancesAsync(new InstanceQuery
            {
                DefinitionId = c.DefinitionId, EntityId = entityId, Entity = entity, Kinds = [InstanceKinds.Automation],
                Statuses = [InstanceStatus.Running, InstanceStatus.Waiting, InstanceStatus.Incident, InstanceStatus.Queued],
            }, ct);
            if (busy > 0)
            {
                if (concurrency == "skipIfRunning")
                {
                    await u.AuditAsync(new AuditEntry
                    {
                        Category = "trigger", Action = "skipped", SubjectType = "definition", SubjectId = c.DefinitionId, DefinitionId = c.DefinitionId,
                        Entity = entity, EntityId = entityId, Data = new Dictionary<string, object?> { ["reason"] = "alreadyRunning", ["trigger"] = trigger.ToValue() },
                    }, ct);
                    return null;
                }
                queued = true;
            }
        }
        var id = NewId();
        var state = new RunState { RootList = "root", Input = input ?? [], Snapshot = snapshot };
        foreach (var v in def.Variables ?? []) state.Vars[v.Key] = Values.FromJson(v.Default);
        state.Tokens.Add(new Token { Id = "root", Frames = [new Frame { List = "root" }] });
        var inst = new InstanceRecord
        {
            Id = id, DefinitionId = c.DefinitionId, DefinitionKey = def.Key, Version = c.Version, Kind = InstanceKinds.Automation, ParentId = parentId,
            Reason = reason, Entity = entity, EntityId = entityId, Status = queued ? InstanceStatus.Queued : InstanceStatus.Running,
            Data = state.ToJson(), TriggerJson = trigger.ToJson(), Depth = depth, InitiatorId = initiator.IsUser ? initiator.Id : null,
            InitiatorName = initiator.IsUser ? initiator.Name : null, CorrelationId = correlationId ?? id, StartedAt = u.Now, UpdatedAt = u.Now,
        };
        await u.S.AddInstanceAsync(inst, ct);
        await u.AuditAsync(new AuditEntry
        {
            Category = "instance", Action = queued ? "queued" : "started", SubjectType = "instance", SubjectId = id, InstanceId = id,
            DefinitionId = c.DefinitionId, Entity = entity, EntityId = entityId, Actor = initiator, CorrelationId = inst.CorrelationId,
            Data = new Dictionary<string, object?> { ["version"] = (double)c.Version, ["trigger"] = trigger.ToValue(), ["parent"] = parentId },
        }, ct);
        u.Log?.Add("instance", queued ? "queued" : "started", id, def.Title);
        var x = Track(new Exec(u, inst, c, state));
        if (!queued && drive) await DriveAsync(x, ct);
        return inst;
    }

    /// <summary>Starts a child run of a lifecycle record (transition steps, onEnter, onExit, SLA). Returns null when the list is empty.</summary>
    internal async Task<InstanceRecord?> StartRunAsync(Uow u, InstanceRecord record, CompiledDefinition c, string listId, string reason,
        TriggerInfo trigger, WorkflowActor initiator, Dictionary<string, object?>? input, CancellationToken ct)
    {
        if (!c.Lists.TryGetValue(listId, out var list) || list.Count == 0) return null;
        var id = NewId();
        var state = new RunState { RootList = listId, Input = input ?? [] };
        foreach (var v in c.Definition.Variables ?? []) state.Vars[v.Key] = Values.FromJson(v.Default);
        state.Tokens.Add(new Token { Id = "root", Frames = [new Frame { List = listId }] });
        var inst = new InstanceRecord
        {
            Id = id, DefinitionId = c.DefinitionId, DefinitionKey = c.Definition.Key, Version = c.Version, Kind = InstanceKinds.Run, ParentId = record.Id,
            Reason = reason, Entity = record.Entity, EntityId = record.EntityId, Status = InstanceStatus.Running, Data = state.ToJson(),
            TriggerJson = trigger.ToJson(), Depth = record.Depth, InitiatorId = initiator.IsUser ? initiator.Id : null,
            InitiatorName = initiator.IsUser ? initiator.Name : null, CorrelationId = record.CorrelationId, StartedAt = u.Now, UpdatedAt = u.Now,
        };
        await u.S.AddInstanceAsync(inst, ct);
        await u.AuditAsync(new AuditEntry
        {
            Category = "instance", Action = "started", SubjectType = "instance", SubjectId = id, InstanceId = id, DefinitionId = c.DefinitionId,
            Entity = record.Entity, EntityId = record.EntityId, Actor = initiator, CorrelationId = record.CorrelationId, CausationId = record.Id,
            Data = new Dictionary<string, object?> { ["run"] = reason, ["trigger"] = trigger.ToValue() },
        }, ct);
        await DriveAsync(Track(new Exec(u, inst, c, state)), ct);
        return inst;
    }

    private static Exec Track(Exec x)
    {
        x.U.Execs[x.I.Id] = x;
        return x;
    }

    /// <summary>The instance as this unit holds it: loaded once, then shared (see <see cref="Uow.Execs"/>).</summary>
    internal async Task<Exec> LoadExecAsync(Uow u, string instanceId, CancellationToken ct)
    {
        if (u.Execs.TryGetValue(instanceId, out var held)) return held;
        var inst = await u.S.GetInstanceAsync(instanceId, ct) ?? throw new WorkflowException("notFound", "That instance does not exist.");
        var c = await CompiledAsync(u, inst.DefinitionId, inst.Version, ct);
        return Track(new Exec(u, inst, c, RunState.FromJson(inst.Data)));
    }

    // ── Driving ───────────────────────────────────────────────────────────

    /// <summary>Runs every active token until all of them wait or finish, then saves the instance.</summary>
    internal async Task DriveAsync(Exec x, CancellationToken ct)
    {
        // Already executing further up the call stack (e.g. a step resumed its own instance): that loop carries on.
        if (!x.U.Driving.Add(x.I.Id)) return;
        try
        {
            if (x.I.Status is InstanceStatus.Running or InstanceStatus.Waiting) x.I.Status = InstanceStatus.Running;
            var budget = Options.MaxStepsPerRun;
            while (!x.Ended)
            {
                var token = x.S.Tokens.FirstOrDefault(t => t.Status == TokenStatus.Active);
                if (token is null) break;
                if (--budget < 0)
                {
                    await RaiseIncidentAsync(x, token, null, "The workflow ran too many steps without waiting (a loop?). It was stopped.", ct);
                    break;
                }
                await StepTokenAsync(x, token, ct);
            }
            if (!x.Ended)
            {
                if (x.S.Tokens.Any(t => t.Waiting == "incident")) x.I.Status = InstanceStatus.Incident;
                else if (x.S.Tokens.Any(t => t.Status == TokenStatus.Waiting)) x.I.Status = InstanceStatus.Waiting;
                else if (x.S.Tokens.All(t => t.Status is TokenStatus.Done or TokenStatus.Cancelled)) await FinishAsync(x, InstanceStatus.Completed, null, ct);
            }
            await SaveAsync(x, ct);
        }
        finally
        {
            x.U.Driving.Remove(x.I.Id);
        }
    }

    internal static async Task SaveAsync(Exec x, CancellationToken ct)
    {
        x.I.Data = x.S.ToJson();
        x.I.UpdatedAt = x.U.Now;
        await x.U.S.UpdateInstanceAsync(x.I, ct);
    }

    private async Task StepTokenAsync(Exec x, Token t, CancellationToken ct)
    {
        if (t.Frames.Count == 0)
        {
            await TokenFinishedAsync(x, t, ct);
            return;
        }
        var f = t.Top;
        var list = x.C.List(f.List);
        if (f.Index >= list.Count)
        {
            t.Frames.RemoveAt(t.Frames.Count - 1);
            await ListFinishedAsync(x, t, f, ct);
            return;
        }
        var step = list[f.Index];
        if (step.Disabled == true)
        {
            await x.U.AuditAsync(StepAudit(x, step, "skipped", new Dictionary<string, object?> { ["reason"] = "disabled" }), ct);
            Advance(t);
            return;
        }
        await ExecuteStepAsync(x, t, f, step, ct);
    }

    internal static void Advance(Token t)
    {
        t.Top.Index++;
        t.Top.Attempt = 1;
    }

    /// <summary>A nested list ran to its end: continue after the step that holds it.</summary>
    private async Task ListFinishedAsync(Exec x, Token t, Frame finished, CancellationToken ct)
    {
        if (t.Frames.Count == 0)
        {
            await TokenFinishedAsync(x, t, ct);
            return;
        }
        var parent = t.Top;
        var container = x.C.List(parent.List)[parent.Index];
        if (container is ForEachStep fe && !fe.IsParallel && finished.List == $"{fe.Id}/each")
        {
            var key = $"{t.Id}:{fe.Id}";
            var loop = x.S.Loops.GetValueOrDefault(key);
            var next = (finished.ItemIndex ?? 0) + 1;
            if (loop is not null && next < loop.Items.Count)
            {
                t.Frames.Add(new Frame { List = $"{fe.Id}/each", Item = loop.Items[next], ItemIndex = next });
                return;
            }
            x.S.Loops.Remove(key);
            x.S.SetOutput(fe.Id, "count", loop?.Items.Count ?? 0);
            await CompleteWaitingStepAsync(x, t, fe, ct);
        }
        Advance(t);
    }

    private async Task TokenFinishedAsync(Exec x, Token t, CancellationToken ct)
    {
        t.Status = TokenStatus.Done;
        if (t.ParentId is null) return;
        var join = t.JoinKey is null ? null : x.S.Joins.GetValueOrDefault(t.JoinKey);
        if (join is null) return;
        join.Done.Add(t.Branch ?? t.Id);
        var parent = x.S.Token(join.TokenId)!;
        var step = x.C.Step(join.StepId)!;
        bool satisfied;
        if (join.Kind == "forEach")
        {
            var running = join.Children.Count(id => x.S.Token(id)?.Status is TokenStatus.Active or TokenStatus.Waiting);
            while (join.Pending is { Count: > 0 } && running < join.Concurrency)
            {
                SpawnItem(x, parent, (ForEachStep)step, join);
                running++;
            }
            satisfied = join.Done.Count >= (join.Items?.Count ?? 0);
            if (satisfied) x.S.SetOutput(step.Id, "count", join.Items?.Count ?? 0);
        }
        else
        {
            satisfied = join.Done.Count >= join.Required;
            if (satisfied) x.S.SetOutput(step.Id, "completed", join.Done.ToList());
        }
        if (!satisfied) return;
        foreach (var childId in join.Children)
        {
            var child = x.S.Token(childId);
            if (child is { Status: TokenStatus.Active or TokenStatus.Waiting }) await CancelTokenAsync(x, child, ct);
        }
        x.S.Joins.Remove(t.JoinKey!);
        parent.Status = TokenStatus.Active;
        parent.Waiting = null;
        await CompleteWaitingStepAsync(x, parent, step, ct);
        Advance(parent);
    }

    internal static void SpawnItem(Exec x, Token parent, ForEachStep fe, JoinState join)
    {
        var index = join.Pending![0];
        join.Pending.RemoveAt(0);
        var child = new Token
        {
            Id = x.S.NewTokenId(), ParentId = parent.Id, JoinKey = $"{parent.Id}:{fe.Id}", Branch = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Frames = [new Frame { List = $"{fe.Id}/each", Item = join.Items![index], ItemIndex = index }],
        };
        join.Children.Add(child.Id);
        x.S.Tokens.Add(child);
    }

    /// <summary>Stops a token and everything it waits on (timers, tasks, child workflows), including its own child tokens.</summary>
    internal async Task CancelTokenAsync(Exec x, Token t, CancellationToken ct)
    {
        foreach (var child in x.S.Tokens.Where(c => c.ParentId == t.Id && c.Status is TokenStatus.Active or TokenStatus.Waiting).ToList())
            await CancelTokenAsync(x, child, ct);
        await ClearWaitsAsync(x, t, ct);
        if (t.StepRecord is not null)
        {
            var rec = (await x.U.S.ListStepsAsync(x.I.Id, ct)).FirstOrDefault(s => s.Id == t.StepRecord);
            if (rec is { Status: StepStatus.Waiting or StepStatus.Running })
            {
                rec.Status = StepStatus.Cancelled;
                rec.EndedAt = x.U.Now;
                await x.U.S.UpdateStepAsync(rec, ct);
            }
            t.StepRecord = null;
        }
        t.Status = TokenStatus.Cancelled;
        t.Waiting = null;
    }

    /// <summary>Removes a token's bookmarks, cancels its open tasks and its waited-for child instances.</summary>
    internal async Task ClearWaitsAsync(Exec x, Token t, CancellationToken ct)
    {
        foreach (var b in (await x.U.S.ListBookmarksAsync(x.I.Id, ct)).Where(b => b.TokenId == t.Id))
        {
            await x.U.S.RemoveBookmarkAsync(b.Id, ct);
            if (b.Kind == BookmarkKinds.Child && b.Payload is { } childId)
            {
                var child = await x.U.S.GetInstanceAsync(childId, ct);
                if (child is not null && !InstanceStatus.IsFinished(child.Status)) await CancelInstanceAsync(x.U, child, x.Actor, "The step that started it was cancelled.", ct);
            }
        }
        foreach (var task in await x.U.S.QueryTasksAsync(new TaskQuery { InstanceId = x.I.Id, Statuses = [Storage.TaskStatus.Open], Take = 1000 }, ct))
        {
            if (task.TokenId != t.Id) continue;
            task.Status = Storage.TaskStatus.Cancelled;
            task.CompletedAt = x.U.Now;
            await x.U.S.UpdateTaskAsync(task, ct);
            await x.U.AuditAsync(TaskAudit(x.U, task, "cancelled", x.Actor), ct);
        }
    }

    // ── Finishing ─────────────────────────────────────────────────────────

    /// <summary>Ends an instance: cancels its live tokens, records the outcome and resumes a waiting parent. <paramref name="by"/> is who ended it (default: the workflow itself).</summary>
    internal async Task FinishAsync(Exec x, string status, string? error, CancellationToken ct, WorkflowActor? by = null)
    {
        foreach (var t in x.S.Tokens.Where(t => t.Status is TokenStatus.Active or TokenStatus.Waiting).ToList()) await CancelTokenAsync(x, t, ct);
        x.Ended = true;
        x.I.Status = status;
        x.I.Error = error ?? x.I.Error;
        x.I.EndedAt = x.U.Now;
        x.U.Finished.Add(x.I);
        var action = status switch { InstanceStatus.Completed => "completed", InstanceStatus.Failed => "failed", _ => "cancelled" };
        await x.U.AuditAsync(new AuditEntry
        {
            Category = "instance", Action = action, SubjectType = "instance", SubjectId = x.I.Id, InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId,
            Entity = x.I.Entity, EntityId = x.I.EntityId, Actor = by ?? x.Actor, CorrelationId = x.I.CorrelationId,
            Data = error is null ? null : new Dictionary<string, object?> { ["reason"] = error },
        }, ct);
        x.U.Log?.Add("instance", action, x.I.Id, error);
        await NotifyParentAsync(x.U, x.I, x.S, ct);
    }

    /// <summary>Cancels an instance from outside (admin, or a parent step that was cancelled).</summary>
    internal async Task CancelInstanceAsync(Uow u, InstanceRecord inst, WorkflowActor actor, string reason, CancellationToken ct)
    {
        if (InstanceStatus.IsFinished(inst.Status)) return;
        if (inst.Kind == InstanceKinds.Record)
        {
            foreach (var run in await u.S.QueryInstancesAsync(new InstanceQuery { ParentId = inst.Id, Take = 1000 }, ct))
                if (!InstanceStatus.IsFinished(run.Status)) await CancelInstanceAsync(u, run, actor, reason, ct);
            foreach (var b in await u.S.ListBookmarksAsync(inst.Id, ct)) await u.S.RemoveBookmarkAsync(b.Id, ct);
            inst.Status = InstanceStatus.Cancelled;
            inst.EndedAt = u.Now;
            inst.UpdatedAt = u.Now;
            await u.S.UpdateInstanceAsync(inst, ct);
            await u.AuditAsync(new AuditEntry
            {
                Category = "instance", Action = "cancelled", SubjectType = "instance", SubjectId = inst.Id, InstanceId = inst.Id, DefinitionId = inst.DefinitionId,
                Entity = inst.Entity, EntityId = inst.EntityId, Actor = actor, CorrelationId = inst.CorrelationId, Data = new Dictionary<string, object?> { ["reason"] = reason },
            }, ct);
            return;
        }
        var x = await LoadExecAsync(u, inst.Id, ct);
        await FinishAsync(x, InstanceStatus.Cancelled, reason, ct, actor);
        await SaveAsync(x, ct);
    }

    /// <summary>Starts the oldest queued instance for each record whose previous instance just finished.</summary>
    private async Task StartQueuedAfterAsync(Uow u, CancellationToken ct)
    {
        // u.Finished grows while this runs: a dequeued instance that finishes at once is looked at too, so the queue drains.
        for (var i = 0; i < u.Finished.Count; i++)
        {
            var done = u.Finished[i];
            if (done.Kind != InstanceKinds.Automation || done.EntityId is null) continue;
            var queued = (await u.S.QueryInstancesAsync(new InstanceQuery
            {
                DefinitionId = done.DefinitionId, Entity = done.Entity, EntityId = done.EntityId, Statuses = [InstanceStatus.Queued], Kinds = [InstanceKinds.Automation], Take = 1000,
            }, ct)).OrderBy(q => q.StartedAt).FirstOrDefault();
            if (queued is null) continue;
            var stillBusy = await u.S.CountInstancesAsync(new InstanceQuery
            {
                DefinitionId = done.DefinitionId, Entity = done.Entity, EntityId = done.EntityId, Kinds = [InstanceKinds.Automation],
                Statuses = [InstanceStatus.Running, InstanceStatus.Waiting, InstanceStatus.Incident],
            }, ct);
            if (stillBusy > 0) continue;
            var x = await LoadExecAsync(u, queued.Id, ct);
            x.I.Status = InstanceStatus.Running;
            await u.AuditAsync(new AuditEntry
            {
                Category = "instance", Action = "started", SubjectType = "instance", SubjectId = x.I.Id, InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId,
                Entity = x.I.Entity, EntityId = x.I.EntityId, Actor = WorkflowActor.System, CorrelationId = x.I.CorrelationId, Data = new Dictionary<string, object?> { ["fromQueue"] = true },
            }, ct);
            await DriveAsync(x, ct);
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    internal static AuditEntry StepAudit(Exec x, Step step, string action, object? data = null) => new()
    {
        Category = "step", Action = action, SubjectType = "step", SubjectId = step.Id, InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId,
        Entity = x.I.Entity, EntityId = x.I.EntityId, Actor = x.Actor, CorrelationId = x.I.CorrelationId,
        Data = data is null ? new Dictionary<string, object?> { ["type"] = step.Type, ["title"] = step.Title } : Merge(new() { ["type"] = step.Type, ["title"] = step.Title }, data),
    };

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> a, object data)
    {
        if (Values.Normalize(data) is Dictionary<string, object?> d) foreach (var kv in d) a[kv.Key] = kv.Value;
        return a;
    }

    internal static AuditEntry TaskAudit(Uow u, TaskRecord t, string action, WorkflowActor actor, object? data = null) => new()
    {
        Category = "task", Action = action, SubjectType = "task", SubjectId = t.Id, InstanceId = t.InstanceId, Entity = t.Entity, EntityId = t.EntityId,
        Actor = actor, CorrelationId = t.RootInstanceId ?? t.InstanceId, Data = data,
    };

    internal async Task<StepRecord> BeginStepAsync(Exec x, Token t, Step step, object? input, CancellationToken ct)
    {
        var rec = new StepRecord
        {
            Id = NewId(), InstanceId = x.I.Id, NodeId = step.Id, StepType = step.Type, TokenId = t.Id, Attempt = t.Top.Attempt,
            Status = StepStatus.Running, Input = input is null ? null : Values.Serialize(input), StartedAt = x.U.Now,
        };
        await x.U.S.AddStepAsync(rec, ct);
        x.Current = rec;
        await x.U.AuditAsync(StepAudit(x, step, "started", t.Top.Attempt > 1 ? new Dictionary<string, object?> { ["attempt"] = (double)t.Top.Attempt } : null), ct);
        x.U.Log?.Add("step", "started", step.Id, step.Title ?? step.Type);
        return rec;
    }

    internal async Task CompleteStepAsync(Exec x, StepRecord rec, Step step, Dictionary<string, object?>? outputs, CancellationToken ct, string status = StepStatus.Completed)
    {
        if (x.Current == rec) x.Current = null;
        rec.Status = status;
        rec.EndedAt = x.U.Now;
        rec.Output = outputs is null ? null : Values.Serialize(outputs);
        await x.U.S.UpdateStepAsync(rec, ct);
        await x.U.AuditAsync(StepAudit(x, step, status == StepStatus.Completed ? "completed" : status, outputs is null ? null : new Dictionary<string, object?> { ["outputs"] = outputs }), ct);
        x.U.Log?.Add("step", status, step.Id, step.Title ?? step.Type);
    }

    /// <summary>Marks the step a token was waiting in as completed, with the outputs gathered so far.</summary>
    internal async Task CompleteWaitingStepAsync(Exec x, Token t, Step step, CancellationToken ct)
    {
        if (t.StepRecord is null) return;
        var rec = (await x.U.S.ListStepsAsync(x.I.Id, ct)).FirstOrDefault(s => s.Id == t.StepRecord);
        t.StepRecord = null;
        if (rec is null) return;
        await CompleteStepAsync(x, rec, step, x.S.Outputs.GetValueOrDefault(step.Id), ct);
    }

    internal static void Wait(Token t, string what, StepRecord? rec)
    {
        t.Status = TokenStatus.Waiting;
        t.Waiting = what;
        if (rec is not null)
        {
            rec.Status = StepStatus.Waiting;
            t.StepRecord = rec.Id;
        }
    }

    internal static void Resume(Token t)
    {
        t.Status = TokenStatus.Active;
        t.Waiting = null;
    }

    internal bool IsWorking(Exec x, string? calendar) => (calendar ?? x.C.Definition.Settings?.Calendar) == "working";

    internal DateTimeOffset Due(Exec x, string iso, string? calendar) => x.U.Calendar.Add(x.U.Now, IsoDuration.Parse(iso), IsWorking(x, calendar));

    internal async Task AddTimerAsync(Exec x, Token t, Step step, string purpose, DateTimeOffset due, string? payload, CancellationToken ct, string? taskId = null)
    {
        await x.U.S.AddBookmarkAsync(new BookmarkRecord
        {
            Id = NewId(), InstanceId = x.I.Id, TokenId = t.Id, NodeId = step.Id, Kind = BookmarkKinds.Timer, Purpose = purpose, DueAt = due, Payload = payload,
            TaskId = taskId, Entity = x.I.Entity, EntityId = x.I.EntityId, DefinitionId = x.I.DefinitionId, CreatedAt = x.U.Now,
        }, ct);
    }

    /// <summary>Renders a template to text: enums as labels, users as names, booleans as Yes/No. <paramref name="encode"/> is applied to values only.</summary>
    internal async Task<string> RenderAsync(ValueScope scope, Template? t, CancellationToken ct, Func<string, string>? encode = null)
    {
        if (t is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var part in t.Parts)
        {
            if (part is not TemplateRef r)
            {
                sb.Append(part);
                continue;
            }
            var text = await RenderValueAsync(scope, r.Ref, ct);
            sb.Append(encode is null ? text : encode(text));
        }
        return sb.ToString();
    }

    private async Task<string> RenderValueAsync(ValueScope scope, string path, CancellationToken ct)
    {
        var value = await scope.ResolveAsync(path, ct);
        var field = scope.FieldOf(path);
        if (field?.Type == FieldTypes.Enum) return field.OptionLabel(value);
        if ((field?.Type == FieldTypes.User || path is "trigger.actor" or "instance.initiator") && value is not null)
            return (await Options.Identity.GetUserAsync(Values.Text(value), ct))?.Name ?? Values.Text(value);
        if (field?.Type == FieldTypes.Relation && value is not null && field.Entity is not null)
            return await RecordTitleAsync(scope, field.Entity, Values.Text(value), ct) ?? Values.Text(value);
        // Dates read the way people write them (spec §3): "20 Oct 2026"; date-times also get a time and zone.
        if (value is string s && (field?.Type == FieldTypes.Date || path == "today")
            && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (value is string t && (field?.Type == FieldTypes.DateTime || path is "now" or "trigger.at" or "instance.startedAt")
            && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return Values.FormatForPeople(at, scope.Zone);
        return Values.Text(value);
    }

    internal async Task<string?> RecordTitleAsync(ValueScope scope, string entity, string id, CancellationToken ct)
    {
        var desc = Registry.Entity(entity);
        if (desc?.TitleField is null) return null;
        var snap = await scope.LoadAsync(entity, id, ct);
        return snap is null ? null : Values.Text(snap[desc.TitleField]);
    }

    /// <summary>Resolves an assignee list to user ids (spec §8).</summary>
    internal async Task<List<string>> ResolveAssigneesAsync(ValueScope scope, IEnumerable<Assignee> list, IReadOnlyList<string>? currentAssignees, string? initiatorId, CancellationToken ct)
    {
        var ids = new List<string>();
        void AddValue(object? v)
        {
            switch (Values.Normalize(v))
            {
                case string s when s.Length > 0: ids.Add(s); break;
                case double d: ids.Add(Values.NumberText(d)); break;
                case List<object?> l: foreach (var x in l) AddValue(x); break;
                case Dictionary<string, object?> m when m.TryGetValue("value", out var mv): AddValue(mv); break;
            }
        }
        foreach (var a in list)
        {
            if (a.Role is not null) ids.AddRange(await Options.Identity.GetUsersInRoleAsync(a.Role, ct));
            else if (a.Group is not null) ids.AddRange(await Options.Identity.GetUsersInGroupAsync(a.Group, ct));
            else if (a.User is not null) ids.Add(a.User);
            else if (a.Field is not null) AddValue(await scope.ResolveAsync(a.Field, ct));
            else if (a.Initiator == true && initiatorId is not null) ids.Add(initiatorId);
            else if (a.ManagerOf is not null)
            {
                var of = new List<string>();
                if (a.ManagerOf == "initiator") { if (initiatorId is not null) of.Add(initiatorId); }
                else if (a.ManagerOf == "assignees") of.AddRange(currentAssignees ?? []);
                else
                {
                    var before = ids.Count;
                    AddValue(await scope.ResolveAsync(a.ManagerOf, ct));
                    of.AddRange(ids.Skip(before));
                    ids.RemoveRange(before, ids.Count - before);
                }
                foreach (var person in of)
                    if (await Options.Identity.GetManagerAsync(person, ct) is { } m) ids.Add(m);
            }
        }
        return ids.Where(i => i.Length > 0).Distinct().ToList();
    }
}
