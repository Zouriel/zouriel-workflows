using System.Globalization;
using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Changes;
using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Engine;

public sealed partial class WorkflowEngine
{
    private async Task ExecuteStepAsync(Exec x, Token t, Frame f, Step step, CancellationToken ct)
    {
        try
        {
            switch (step)
            {
                case BranchStep b: await BranchAsync(x, t, b, ct); break;
                case SwitchStep sw: await SwitchAsync(x, t, sw, ct); break;
                case ParallelStep p: await ParallelAsync(x, t, p, ct); break;
                case ForEachStep fe: await ForEachAsync(x, t, fe, ct); break;
                case HumanStep h: await CreateTaskAsync(x, t, h, ct); break;
                case WaitStep w: await WaitAsync(x, t, w, ct); break;
                case SetFieldsStep sf: await SetFieldsAsync(x, t, sf, ct); break;
                case SetVariablesStep sv: await SetVariablesAsync(x, t, sv, ct); break;
                case ApplyTransitionStep at: await ApplyTransitionStepAsync(x, t, at, ct); break;
                case NotifyStep n: await NotifyAsync(x, t, n, ct); break;
                case WebhookStep wh: await WebhookAsync(x, t, wh, ct); break;
                case ActionStep a: await ActionAsync(x, t, a, ct); break;
                case SubWorkflowStep sub: await SubWorkflowAsync(x, t, sub, ct); break;
                case GoToStep g: await GoToAsync(x, t, g, ct); break;
                case EndStep e: await EndAsync(x, t, e, ct); break;
                default: throw new WorkflowException("unknownStepType", $"This engine cannot run '{step.Type}' steps.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not WorkflowConcurrencyException)
        {
            await HandleStepErrorAsync(x, t, step, e, null, ct);
        }
    }

    // ── Flow ──────────────────────────────────────────────────────────────

    private async Task BranchAsync(Exec x, Token t, BranchStep b, CancellationToken ct)
    {
        var rec = await BeginStepAsync(x, t, b, null, ct);
        var scope = x.Scope(t);
        string? taken = null;
        string? list = null;
        foreach (var p in b.Paths)
        {
            if (!await scope.ConditionAsync(p.When, ct)) continue;
            taken = p.Id;
            list = $"{b.Id}/path:{p.Id}";
            break;
        }
        if (taken is null && b.Otherwise is not null)
        {
            taken = b.Otherwise.Id;
            list = $"{b.Id}/otherwise";
        }
        x.S.SetOutput(b.Id, "path", taken);
        await x.U.AuditAsync(StepAudit(x, b, "branchTaken", new Dictionary<string, object?> { ["path"] = taken }), ct);
        await CompleteStepAsync(x, rec, b, x.S.Outputs[b.Id], ct);
        if (list is null) Advance(t);
        else t.Frames.Add(new Frame { List = list });
    }

    private async Task SwitchAsync(Exec x, Token t, SwitchStep sw, CancellationToken ct)
    {
        var rec = await BeginStepAsync(x, t, sw, null, ct);
        var scope = x.Scope(t);
        var value = Conditions.OptionValueOf(await scope.ResolveAsync(sw.On, ct));
        SwitchCase? match = null;
        foreach (var c in sw.Cases)
        {
            var rule = new ConditionRule { Ref = "$v", Op = "anyOf", Value = Operand.Of(c.Values.Select(Values.FromJson).ToList()) };
            if (Conditions.Evaluate(rule, new FixedValue(value, scope.Today))) { match = c; break; }
        }
        var taken = match?.Id ?? sw.Otherwise?.Id;
        x.S.SetOutput(sw.Id, "case", taken);
        await x.U.AuditAsync(StepAudit(x, sw, "branchTaken", new Dictionary<string, object?> { ["path"] = taken }), ct);
        await CompleteStepAsync(x, rec, sw, x.S.Outputs[sw.Id], ct);
        if (match is not null) t.Frames.Add(new Frame { List = $"{sw.Id}/case:{match.Id}" });
        else if (sw.Otherwise is not null) t.Frames.Add(new Frame { List = $"{sw.Id}/otherwise" });
        else Advance(t);
    }

    private sealed class FixedValue(object? v, string today) : IValueContext
    {
        public object? Read(string path) => v;
        public string Today { get; } = today;
    }

    private async Task ParallelAsync(Exec x, Token t, ParallelStep p, CancellationToken ct)
    {
        var rec = await BeginStepAsync(x, t, p, null, ct);
        var key = $"{t.Id}:{p.Id}";
        var (item, itemIndex) = t.CurrentItem();
        var join = new JoinState
        {
            StepId = p.Id, TokenId = t.Id, Kind = "parallel",
            Required = p.Join.Type switch { "any" => 1, "count" => Math.Clamp(p.Join.Count ?? 1, 1, p.Paths.Count), _ => p.Paths.Count },
        };
        foreach (var path in p.Paths)
        {
            var child = new Token
            {
                Id = x.S.NewTokenId(), ParentId = t.Id, JoinKey = key, Branch = path.Id,
                Frames = [new Frame { List = $"{p.Id}/path:{path.Id}", Item = item, ItemIndex = itemIndex }],
            };
            join.Children.Add(child.Id);
            x.S.Tokens.Add(child);
        }
        x.S.Joins[key] = join;
        Wait(t, "join", rec);
    }

    private async Task ForEachAsync(Exec x, Token t, ForEachStep fe, CancellationToken ct)
    {
        var rec = await BeginStepAsync(x, t, fe, null, ct);
        var scope = x.Scope(t);
        var (owner, ownerId, field) = await CollectionOwnerAsync(scope, fe.Collection, ct);
        var items = new List<Dictionary<string, object?>>();
        if (owner is not null && ownerId is not null && field is not null)
        {
            var max = Math.Min(fe.Max, x.U.Settings.ForEachMax);
            var related = Registry.Field(owner, field)?.Entity;
            foreach (var snap in await x.U.S.Entities.GetCollectionAsync(owner, ownerId, field, ct))
            {
                var item = new Dictionary<string, object?>(snap.Values, StringComparer.Ordinal) { ["id"] = snap.Id, ["$entity"] = related };
                if (fe.When is not null)
                {
                    var itemScope = x.U.Scope(x.I, x.S, new Token { Frames = [new Frame { Item = item, ItemIndex = items.Count }] });
                    if (!await itemScope.ConditionAsync(fe.When, ct)) continue;
                }
                items.Add(item);
                if (items.Count >= max) break;
            }
        }
        if (items.Count == 0)
        {
            x.S.SetOutput(fe.Id, "count", 0);
            await CompleteStepAsync(x, rec, fe, x.S.Outputs[fe.Id], ct);
            Advance(t);
            return;
        }
        if (!fe.IsParallel)
        {
            x.S.Loops[$"{t.Id}:{fe.Id}"] = new LoopState { Items = items };
            t.StepRecord = rec.Id;
            rec.Status = StepStatus.Waiting;
            await x.U.S.UpdateStepAsync(rec, ct);
            t.Frames.Add(new Frame { List = $"{fe.Id}/each", Item = items[0], ItemIndex = 0 });
            return;
        }
        var join = new JoinState
        {
            StepId = fe.Id, TokenId = t.Id, Kind = "forEach", Items = items, Pending = Enumerable.Range(0, items.Count).ToList(),
            Concurrency = Math.Clamp(fe.Concurrency ?? 5, 1, 20),
        };
        x.S.Joins[$"{t.Id}:{fe.Id}"] = join;
        while (join.Pending.Count > 0 && join.Children.Count < join.Concurrency) SpawnItem(x, t, fe, join);
        Wait(t, "join", rec);
    }

    /// <summary>For a collection path like <c>entity.documents</c> or <c>entity.programme.courses</c>: the record that owns it.</summary>
    private async Task<(string? Entity, string? Id, string? Field)> CollectionOwnerAsync(ValueScope scope, string path, CancellationToken ct)
    {
        var parts = path.Split('.');
        string? entity = parts[0] == "item" ? scope.ItemEntity : scope.Entity;
        string? id = parts[0] == "item" ? scope.Item?.GetValueOrDefault("id") as string : scope.EntityId;
        for (var i = 1; i < parts.Length - 1 && entity is not null && id is not null; i++)
        {
            var f = Registry.Field(entity, parts[i]);
            if (f?.Type != FieldTypes.Relation) return (null, null, null);
            var snap = await scope.LoadAsync(entity, id, ct);
            id = snap?[f.Key] is { } rid ? Values.Text(rid) : null;
            entity = f.Entity;
        }
        return (entity, id, parts.Length > 1 ? parts[^1] : null);
    }

    private async Task GoToAsync(Exec x, Token t, GoToStep g, CancellationToken ct)
    {
        var count = x.S.GoTo.GetValueOrDefault($"{t.Id}:{g.Id}");
        if (count >= g.MaxTimes)
        {
            await x.U.AuditAsync(StepAudit(x, g, "skipped", new Dictionary<string, object?> { ["reason"] = "maxTimes", ["count"] = (double)count }), ct);
            Advance(t);
            return;
        }
        var loc = x.C.Steps.GetValueOrDefault(g.Target) ?? throw new WorkflowException("goToInvalidTarget", "The step to go back to no longer exists.");
        var depth = t.Frames.FindLastIndex(fr => fr.List == loc.ListId);
        if (depth < 0) throw new WorkflowException("goToInvalidTarget", "Go back can only return to an earlier step on the same path.");
        x.S.GoTo[$"{t.Id}:{g.Id}"] = count + 1;
        x.S.SetOutput(g.Id, "count", count + 1);
        await x.U.AuditAsync(StepAudit(x, g, "completed", new Dictionary<string, object?> { ["target"] = g.Target, ["count"] = (double)(count + 1) }), ct);
        // Leaving nested lists drops their loop state.
        for (var i = t.Frames.Count - 1; i > depth; i--) t.Frames.RemoveAt(i);
        foreach (var k in x.S.Loops.Keys.Where(k => k.StartsWith(t.Id + ":", StringComparison.Ordinal)).ToList())
        {
            var stepId = k[(t.Id.Length + 1)..];
            if (x.C.Steps.TryGetValue(stepId, out var l) && l.ListId == loc.ListId && l.Index >= loc.Index) x.S.Loops.Remove(k);
        }
        t.Top.Index = loc.Index;
        t.Top.Attempt = 1;
    }

    private async Task EndAsync(Exec x, Token t, EndStep e, CancellationToken ct)
    {
        var reason = e.Reason is null ? null : await RenderAsync(x.Scope(t), e.Reason, ct);
        await x.U.AuditAsync(StepAudit(x, e, "completed", new Dictionary<string, object?> { ["result"] = e.Result, ["reason"] = reason }), ct);
        await FinishAsync(x, e.Result == "failed" ? InstanceStatus.Failed : InstanceStatus.Completed, reason, ct);
    }

    // ── Waiting ───────────────────────────────────────────────────────────

    private async Task WaitAsync(Exec x, Token t, WaitStep w, CancellationToken ct)
    {
        var rec = await BeginStepAsync(x, t, w, null, ct);
        var u = w.Until;
        var scope = x.Scope(t);
        switch (u.Type)
        {
            case "duration":
            {
                var due = Due(x, u.Duration!, u.Calendar);
                await AddTimerAsync(x, t, w, BookmarkPurposes.Wait, due, null, ct);
                Wait(t, "timer", rec);
                break;
            }
            case "date":
            {
                var due = DateWaitDue(x.U.Calendar, await scope.OperandAsync(u.Date, ct), u.Offset, u.At);
                if (due is null || due <= x.U.Now)
                {
                    x.S.SetOutput(w.Id, "timedOut", false);
                    await CompleteStepAsync(x, rec, w, x.S.Outputs[w.Id], ct);
                    Advance(t);
                    return;
                }
                await AddTimerAsync(x, t, w, BookmarkPurposes.Wait, due.Value, null, ct);
                Wait(t, "timer", rec);
                break;
            }
            case "condition":
            {
                if (await scope.ConditionAsync(u.When, ct))
                {
                    x.S.SetOutput(w.Id, "timedOut", false);
                    await CompleteStepAsync(x, rec, w, x.S.Outputs[w.Id], ct);
                    Advance(t);
                    return;
                }
                await x.U.S.AddBookmarkAsync(new BookmarkRecord
                {
                    Id = NewId(), InstanceId = x.I.Id, TokenId = t.Id, NodeId = w.Id, Kind = BookmarkKinds.Condition, Purpose = BookmarkPurposes.Condition,
                    Entity = x.I.Entity, EntityId = x.I.EntityId, DefinitionId = x.I.DefinitionId, CreatedAt = x.U.Now,
                }, ct);
                await AddTimerAsync(x, t, w, BookmarkPurposes.Timeout, Due(x, u.Timeout!, null), null, ct);
                Wait(t, "condition", rec);
                break;
            }
            case "signal":
            {
                await x.U.S.AddBookmarkAsync(new BookmarkRecord
                {
                    Id = NewId(), InstanceId = x.I.Id, TokenId = t.Id, NodeId = w.Id, Kind = BookmarkKinds.Signal, Purpose = BookmarkPurposes.Signal, Signal = u.Signal,
                    Entity = x.I.Entity, EntityId = x.I.EntityId, DefinitionId = x.I.DefinitionId, CreatedAt = x.U.Now,
                }, ct);
                if (u.Timeout is not null) await AddTimerAsync(x, t, w, BookmarkPurposes.Timeout, Due(x, u.Timeout, null), null, ct);
                Wait(t, "signal", rec);
                break;
            }
            default:
                throw new WorkflowException("badWait", $"Unknown wait type '{u.Type}'.");
        }
        await x.U.S.UpdateStepAsync(rec, ct);
        await x.U.AuditAsync(StepAudit(x, w, "waiting", new Dictionary<string, object?> { ["until"] = u.Type }), ct);
    }

    /// <summary>When a date or date-time value plus an offset arrives (date values land at <paramref name="at"/>, default 09:00, in the settings time zone).</summary>
    internal static DateTimeOffset? DateWaitDue(WorkCalendar calendar, object? value, string? offset, string? at)
    {
        if (value is not string s || s.Length < 10) return null;
        DateTimeOffset baseTime;
        if (s.Length == 10)
        {
            if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
            var time = at is not null && TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var tt) ? tt : new TimeOnly(9, 0);
            baseTime = calendar.AtLocal(d, time);
        }
        else if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out baseTime)) return null;
        return offset is null ? baseTime : calendar.Add(baseTime, IsoDuration.Parse(offset));
    }

    // ── Data ──────────────────────────────────────────────────────────────

    private async Task SetFieldsAsync(Exec x, Token t, SetFieldsStep sf, CancellationToken ct)
    {
        var scope = x.Scope(t);
        var (entity, id) = await TargetAsync(scope, sf.Target, ct);
        if (entity is null || id is null) throw new WorkflowException("noRecord", "There is no record to update.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var v in sf.Values)
        {
            var field = Registry.Field(entity, v.Field);
            if (field is null || !field.IsSettable) throw new WorkflowException("fieldNotSettable", $"Workflows cannot change '{v.Field}'.");
            values[v.Field] = await scope.OperandAsync(v.Value, ct);
        }
        var masked = (await x.U.MaskerAsync(entity, id, ct)).MaskMap(values);
        var rec = await BeginStepAsync(x, t, sf, new Dictionary<string, object?> { ["entity"] = entity, ["id"] = id, ["values"] = masked }, ct);
        using (WorkflowWriteScope.Begin(x.Actor, x.I.Depth + 1, x.I.Id, x.I.DefinitionId, x.I.CorrelationId))
        {
            await x.U.S.Entities.SetFieldsAsync(entity, id, values, ct);
        }
        x.U.ForgetMasker(entity, id);
        await CompleteStepAsync(x, rec, sf, null, ct);
        Advance(t);
    }

    internal async Task<(string? Entity, string? Id)> TargetAsync(ValueScope scope, string? target, CancellationToken ct)
    {
        if (target is null or "self") return (scope.Entity, scope.EntityId);
        // Inside forEach: the current item of the collection.
        if (target == "item") return (scope.ItemEntity, scope.Item?.GetValueOrDefault("id") as string);
        var (entity, id, field) = await CollectionOwnerAsync(scope, target, ct);
        if (entity is null || id is null || field is null) return (null, null);
        var f = Registry.Field(entity, field);
        if (f?.Type != FieldTypes.Relation) return (null, null);
        var snap = await scope.LoadAsync(entity, id, ct);
        var related = snap?[field];
        return (f.Entity, related is null ? null : Values.Text(related));
    }

    private async Task SetVariablesAsync(Exec x, Token t, SetVariablesStep sv, CancellationToken ct)
    {
        var scope = x.Scope(t);
        var changed = new Dictionary<string, object?>();
        foreach (var v in sv.Values)
        {
            var key = x.C.VariableKey(v.Variable);
            var value = await scope.OperandAsync(v.Value, ct);
            x.S.Vars[key] = value;
            changed[key] = value;
        }
        await x.U.AuditAsync(StepAudit(x, sv, "completed", new Dictionary<string, object?> { ["vars"] = changed }), ct);
        Advance(t);
    }

    private async Task ApplyTransitionStepAsync(Exec x, Token t, ApplyTransitionStep at, CancellationToken ct)
    {
        var scope = x.Scope(t);
        var (entity, id) = await TargetAsync(scope, at.Target, ct);
        if (entity is null || id is null) throw new WorkflowException("noRecord", "There is no record to move.");
        var input = new Dictionary<string, object?>();
        foreach (var (k, o) in at.Input ?? []) input[k] = await scope.OperandAsync(o, ct);
        var rec = await BeginStepAsync(x, t, at, new Dictionary<string, object?> { ["entity"] = entity, ["id"] = id, ["transition"] = at.Transition }, ct);
        bool applied;
        try
        {
            using (WorkflowWriteScope.Begin(x.Actor, x.I.Depth + 1, x.I.Id, x.I.DefinitionId, x.I.CorrelationId))
                await ApplyTransitionCoreAsync(x.U, at.Lifecycle, entity, id, at.Transition, input, x.Actor, isPerson: false, auto: false, x.I.Depth + 1, ct);
            applied = true;
        }
        catch (WorkflowException e) when (at.IfNotAllowed == "skip" && e.Code is "notInFromState" or "guardFailed" or "notAllowed")
        {
            applied = false;
        }
        x.S.SetOutput(at.Id, "applied", applied);
        await CompleteStepAsync(x, rec, at, x.S.Outputs[at.Id], ct);
        scope.Invalidate();
        Advance(t);
    }

    // ── Calling out ───────────────────────────────────────────────────────

    private async Task NotifyAsync(Exec x, Token t, NotifyStep n, CancellationToken ct)
    {
        var scope = x.Scope(t);
        var recipients = await RecipientsAsync(x, scope, n.To, ct);
        var subject = n.Subject is null ? null : await RenderAsync(scope, n.Subject, ct);
        var body = await RenderAsync(scope, n.Body, ct);
        var footer = x.U.Settings.Channels?.GetValueOrDefault(n.Channel)?.Footer;
        if (!string.IsNullOrWhiteSpace(footer)) body += "\n\n" + footer;
        string? link = null;
        if (n.Link is { } l)
            link = l.Parts is [string only] && only == "record" ? RecordLink(x.I.Entity, x.I.EntityId) : await RenderAsync(scope, l, ct);
        var rec = await BeginStepAsync(x, t, n, new Dictionary<string, object?> { ["channel"] = n.Channel, ["to"] = recipients.Select(r => r.Name ?? r.Email ?? r.UserId).ToList(), ["subject"] = subject }, ct);
        var key = $"{x.I.Id}:{n.Id}:{t.Id}:{t.CurrentItem().Index}:{x.S.GoTo.Values.Sum()}";
        if (recipients.Count > 0) await QueueNotificationAsync(x.U, new WorkflowNotification(n.Channel, recipients, subject, body, link, x.I.Id, x.I.Entity, x.I.EntityId, key), ct);
        x.S.SetOutput(n.Id, "sent", recipients.Count);
        await CompleteStepAsync(x, rec, n, x.S.Outputs[n.Id], ct);
        Advance(t);
    }

    internal string? RecordLink(string? entity, string? id) =>
        Options.RecordLinkTemplate is null || entity is null || id is null ? null : Options.RecordLinkTemplate.Replace("{entity}", entity).Replace("{id}", Uri.EscapeDataString(id));

    internal async Task<List<NotificationRecipient>> RecipientsAsync(Exec x, ValueScope scope, List<Assignee> to, CancellationToken ct)
    {
        var list = new List<NotificationRecipient>();
        var people = await ResolveAssigneesAsync(scope, to.Where(a => a.Email is null), null, x.I.InitiatorId, ct);
        foreach (var id in people)
        {
            var user = await Options.Identity.GetUserAsync(id, ct);
            list.Add(new NotificationRecipient(id, user?.Email, user?.Name ?? id));
        }
        foreach (var a in to.Where(a => a.Email is not null))
        {
            var text = await RenderAsync(scope, a.Email, ct);
            foreach (var addr in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (addr.Contains('@')) list.Add(new NotificationRecipient(null, addr, addr));
        }
        return list.DistinctBy(r => r.UserId ?? r.Email).ToList();
    }

    /// <summary>Notifications go out through the outbox after commit, so a rolled-back step sends nothing.</summary>
    internal async Task QueueNotificationAsync(Uow u, WorkflowNotification n, CancellationToken ct)
    {
        if (u.Simulation)
        {
            u.Log?.Add("notify", n.Channel, n.InstanceId, $"{n.Subject} → {string.Join(", ", n.To.Select(r => r.Name ?? r.Email))}", n);
            return;
        }
        await u.S.AddOutboxAsync(new OutboxRecord
        {
            Id = NewId(), Kind = OutboxKinds.Notify, Payload = System.Text.Json.JsonSerializer.Serialize(n, Definitions.JsonOptions), CreatedAt = u.Now,
        }, ct);
    }

    /// <summary>
    /// The address, headers and body of a webhook call. The address must stay on the named connection's host
    /// or match an allowed origin; values from records go into the path URL-encoded, so they cannot add path
    /// segments, a query or another host.
    /// </summary>
    private async Task<(string Url, Dictionary<string, string> Headers, string? Body)> BuildWebhookAsync(Exec x, Token t, WebhookStep wh, CancellationToken ct)
    {
        var scope = x.Scope(t);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = wh.Path is null ? "" : await RenderAsync(scope, wh.Path, ct, Uri.EscapeDataString);
        string url;
        if (wh.Connection is not null)
        {
            var conn = x.U.Settings.Webhooks?.Connections?.FirstOrDefault(c => c.Key == wh.Connection)
                       ?? throw new WorkflowException("unknownConnection", $"There is no connection called '{wh.Connection}'.");
            url = conn.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
            if (conn.Auth == "secret" && conn.SecretName is not null && !x.U.Simulation)
            {
                var secret = await Options.Secrets.GetSecretAsync(conn.SecretName, ct) ?? throw new WorkflowException("missingSecret", $"The secret for '{conn.Label}' is not set up.");
                headers[conn.Header ?? "Authorization"] = conn.Header is null ? $"Bearer {secret}" : secret;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var connUri) || connUri.Authority != new Uri(conn.BaseUrl).Authority)
                throw new WorkflowException("webhookOriginNotAllowed", "The webhook path must stay on the connection's address.");
        }
        else
        {
            url = wh.Url! + path;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new WorkflowException("webhookOriginNotAllowed", $"'{url}' is not a web address.");
            var origin = $"{uri.Scheme}://{uri.Authority}";
            if (x.U.Settings.Webhooks?.AllowedOrigins?.Contains(origin, StringComparer.OrdinalIgnoreCase) != true)
                throw new WorkflowException("webhookOriginNotAllowed", $"{origin} is not an allowed address.");
        }
        foreach (var (k, v) in wh.Headers ?? []) headers[k] = await RenderAsync(scope, v, ct);
        var body = wh.Body is null ? null : (await SubstituteAsync(scope, wh.Body.DeepClone(), ct))?.ToJsonString();
        return (url, headers, body);
    }

    private async Task WebhookAsync(Exec x, Token t, WebhookStep wh, CancellationToken ct)
    {
        var (url, headers, body) = await BuildWebhookAsync(x, t, wh, ct);
        var key = $"{x.I.Id}:{wh.Id}:{t.Id}:{t.CurrentItem().Index}:{t.Top.Attempt}";
        var rec = await BeginStepAsync(x, t, wh, new Dictionary<string, object?> { ["method"] = wh.Method, ["url"] = url }, ct);
        WebhookResponse res;
        if (x.U.Simulation)
        {
            x.U.Log?.Add("webhook", wh.Method, x.I.Id, url);
            res = new WebhookResponse(200, null);
        }
        else
        {
            var timeout = wh.Timeout is null ? TimeSpan.FromSeconds(30) : IsoDuration.Parse(wh.Timeout).Approximate;
            try
            {
                res = await Options.Webhooks.SendAsync(new WebhookRequest(wh.Method, url, headers, body, key, timeout), ct);
            }
            // A timeout fails the attempt (and may be retried); the host shutting down stops the unit instead.
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                await HandleStepErrorAsync(x, t, wh, e, rec, ct);
                return;
            }
        }
        x.S.SetOutput(wh.Id, "status", res.Status);
        object? parsed = res.Body;
        if (res.Body is { Length: > 0 })
        {
            try { parsed = Values.FromJson(res.Body); }
            catch (System.Text.Json.JsonException) { parsed = res.Body; }
        }
        x.S.SetOutput(wh.Id, "body", parsed);
        var ok = wh.Expect is { Count: > 0 } ? wh.Expect.Contains(res.Status) : res.Status is >= 200 and < 300;
        if (!ok)
        {
            await HandleStepErrorAsync(x, t, wh, new WorkflowException("webhookFailed", $"The webhook answered {res.Status}."), rec, ct);
            return;
        }
        await CompleteStepAsync(x, rec, wh, x.S.Outputs[wh.Id], ct);
        Advance(t);
    }

    /// <summary>Replaces <c>{"ref": "path"}</c> objects in a webhook body with their values.</summary>
    private static async Task<JsonNode?> SubstituteAsync(ValueScope scope, JsonNode? n, CancellationToken ct)
    {
        switch (n)
        {
            case JsonObject o when o.Count == 1 && o["ref"] is JsonValue v && v.TryGetValue<string>(out var path):
                return Values.ToJson(await scope.ResolveAsync(path, ct));
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).ToList()) o[k] = await SubstituteAsync(scope, o[k]?.DeepClone(), ct);
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) a[i] = await SubstituteAsync(scope, a[i]?.DeepClone(), ct);
                return a;
        }
        return n;
    }

    private async Task ActionAsync(Exec x, Token t, ActionStep a, CancellationToken ct)
    {
        var desc = Registry.Action(a.Action) ?? throw new WorkflowException("unknownAction", $"'{a.Action}' is not an action the app offers.");
        var scope = x.Scope(t);
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, o) in a.Input ?? []) input[k] = await scope.OperandAsync(o, ct);
        foreach (var p in desc.Inputs.Where(p => p.Required == true))
            if (Values.IsEmpty(input.GetValueOrDefault(p.Key))) throw new WorkflowException("missingInput", $"'{desc.Label}' needs {p.Label}.");
        var rec = await BeginStepAsync(x, t, a, input, ct);
        IReadOnlyDictionary<string, object?> outputs;
        if (x.U.Simulation)
        {
            x.U.Log?.Add("action", a.Action, x.I.Id, desc.Label, input);
            outputs = desc.Outputs.ToDictionary(o => o.Key, _ => (object?)null);
        }
        else
        {
            var handler = Options.Actions.Resolve(desc) ?? throw new WorkflowException("actionUnavailable", $"No handler is registered for '{desc.Label}'.");
            outputs = await RunActionAsync(x, t, a, handler, input, ct);
        }
        foreach (var (k, v) in outputs) x.S.SetOutput(a.Id, k, v);
        await CompleteStepAsync(x, rec, a, x.S.Outputs.GetValueOrDefault(a.Id), ct);
        scope.Invalidate();
        Advance(t);
    }

    /// <summary>
    /// Calls an action handler inside a savepoint: if it fails, whatever it wrote is undone before the step
    /// fails (spec §7: a failed step changes nothing). A timeout of the step's own fails the attempt.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, object?>> RunActionAsync(Exec x, Token t, ActionStep a, IWorkflowAction handler, Dictionary<string, object?> input, CancellationToken ct)
    {
        var savepoint = "wf_action_" + NewId();
        var undoable = await x.U.S.CreateSavepointAsync(savepoint, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (a.Timeout is not null) cts.CancelAfter(IsoDuration.Parse(a.Timeout).Approximate);
        try
        {
            IReadOnlyDictionary<string, object?> outputs;
            using (WorkflowWriteScope.Begin(x.Actor, x.I.Depth + 1, x.I.Id, x.I.DefinitionId, x.I.CorrelationId))
            {
                outputs = await handler.ExecuteAsync(new WorkflowActionContext
                {
                    ActionKey = a.Action, Input = input, Entity = x.I.Entity, EntityId = x.I.EntityId, InstanceId = x.I.Id,
                    IdempotencyKey = $"{x.I.Id}:{a.Id}:{t.Id}:{t.CurrentItem().Index}:{t.Top.Attempt}", Attempt = t.Top.Attempt,
                    Initiator = x.I.InitiatorId is null ? null : WorkflowActor.User(x.I.InitiatorId, x.I.InitiatorName),
                }, cts.Token);
            }
            if (undoable) await x.U.S.ReleaseSavepointAsync(savepoint, ct);
            return outputs;
        }
        catch (Exception e) when (e is not WorkflowConcurrencyException && !ct.IsCancellationRequested)
        {
            if (undoable) await x.U.S.RollbackToSavepointAsync(savepoint, ct);
            if (e is OperationCanceledException) throw new WorkflowException("timeout", $"'{a.Title ?? a.Action}' took longer than {a.Timeout}.");
            throw;
        }
        finally
        {
            if (x.I.Entity is not null && x.I.EntityId is not null) x.U.ForgetMasker(x.I.Entity, x.I.EntityId);
        }
    }

    private async Task SubWorkflowAsync(Exec x, Token t, SubWorkflowStep sub, CancellationToken ct)
    {
        var defRec = await x.U.S.GetDefinitionByKeyAsync(sub.Workflow, ct);
        if (defRec is null || defRec.Version == 0 || defRec.Status == DefinitionStatus.Archived)
            throw new WorkflowException("unknownWorkflow", $"There is no published workflow called '{sub.Workflow}'.");
        var child = await CompiledAsync(x.U, defRec.Id, defRec.Version, ct);
        var scope = x.Scope(t);
        var (entity, id) = await TargetAsync(scope, sub.Target, ct);
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, o) in sub.Input ?? []) input[k] = await scope.OperandAsync(o, ct);
        var rec = await BeginStepAsync(x, t, sub, new Dictionary<string, object?> { ["workflow"] = sub.Workflow, ["wait"] = sub.Wait }, ct);
        var trigger = new TriggerInfo { Type = "subWorkflow", ActorType = "workflow", ActorId = x.I.Id, ActorName = x.I.DefinitionKey, At = Values.FormatInstant(x.U.Now) };
        var bookmarkId = NewId();
        // The child is created now but runs after this instance has been saved (see Uow.Deferred),
        // so a child that finishes straight away resumes a parent that is already waiting for it.
        var started = await StartAutomationAsync(x.U, child, entity, id, trigger, x.Actor, x.I.Depth + 1, input,
            sub.Wait ? x.I.Id : null, sub.Wait ? $"sub:{sub.Id}:{t.Id}:{bookmarkId}" : null, x.I.CorrelationId, ct, drive: false);
        x.S.SetOutput(sub.Id, "instanceId", started?.Id);
        if (started is null)
        {
            if (sub.Wait) throw new WorkflowException("subWorkflowSkipped", $"'{sub.Workflow}' did not start (it is already running for this record).");
            x.S.SetOutput(sub.Id, "status", "skipped");
            await CompleteStepAsync(x, rec, sub, x.S.Outputs[sub.Id], ct);
            Advance(t);
            return;
        }
        if (started.Status != InstanceStatus.Queued) x.U.Deferred.Add(started.Id);
        if (!sub.Wait)
        {
            x.S.SetOutput(sub.Id, "status", started.Status);
            await CompleteStepAsync(x, rec, sub, x.S.Outputs[sub.Id], ct);
            Advance(t);
            return;
        }
        await x.U.S.AddBookmarkAsync(new BookmarkRecord
        {
            Id = bookmarkId, InstanceId = x.I.Id, TokenId = t.Id, NodeId = sub.Id, Kind = BookmarkKinds.Child, Purpose = BookmarkPurposes.Child, Payload = started.Id, CreatedAt = x.U.Now,
        }, ct);
        Wait(t, $"child:{started.Id}", rec);
        await x.U.S.UpdateStepAsync(rec, ct);
    }

    /// <summary>When a waited-for sub-workflow finishes, its parent step continues.</summary>
    internal async Task NotifyParentAsync(Uow u, InstanceRecord child, RunState childState, CancellationToken ct)
    {
        if (child.Kind != InstanceKinds.Automation || child.ParentId is null || child.Reason?.StartsWith("sub:", StringComparison.Ordinal) != true) return;
        var parts = child.Reason.Split(':');
        var (stepId, tokenId, bookmarkId) = (parts[1], parts[2], parts[3]);
        var bm = await u.S.GetBookmarkAsync(bookmarkId, ct);
        if (bm is null) return;
        await u.S.RemoveBookmarkAsync(bookmarkId, ct);
        var x = await LoadExecAsync(u, child.ParentId, ct);
        var t = x.S.Token(tokenId);
        var step = x.C.Step(stepId);
        if (t is null || step is null || t.Status != TokenStatus.Waiting) return;
        x.S.SetOutput(stepId, "instanceId", child.Id);
        x.S.SetOutput(stepId, "status", child.Status);
        x.S.SetOutput(stepId, "outputs", childState.Vars);
        Resume(t);
        if (child.Status != InstanceStatus.Completed)
        {
            await HandleStepErrorAsync(x, t, step, new WorkflowException("subWorkflowFailed", $"'{child.DefinitionKey}' {child.Status}: {child.Error}"), null, ct);
        }
        else
        {
            await CompleteWaitingStepAsync(x, t, step, ct);
            Advance(t);
        }
        await DriveAsync(x, ct);
    }

    // ── Errors ────────────────────────────────────────────────────────────

    internal async Task HandleStepErrorAsync(Exec x, Token t, Step step, Exception error, StepRecord? rec, CancellationToken ct)
    {
        var message = error is WorkflowException we ? we.Message : $"{error.GetType().Name}: {error.Message}";
        var attempt = t.Top.Attempt;
        rec ??= x.Current is { } cur && cur.NodeId == step.Id ? cur : null;
        rec ??= t.StepRecord is null ? null : (await x.U.S.ListStepsAsync(x.I.Id, ct)).FirstOrDefault(s => s.Id == t.StepRecord);
        x.Current = null;
        t.StepRecord = null;
        if (rec is not null)
        {
            rec.Status = StepStatus.Failed;
            rec.Error = message;
            rec.EndedAt = x.U.Now;
            await x.U.S.UpdateStepAsync(rec, ct);
        }
        await x.U.AuditAsync(StepAudit(x, step, "failed", new Dictionary<string, object?> { ["error"] = message, ["attempt"] = (double)attempt }), ct);
        x.U.Log?.Add("step", "failed", step.Id, message);

        var retry = step.Retry ?? (step is ActionStep or WebhookStep or NotifyStep or SubWorkflowStep ? x.U.Settings.Defaults?.Retry : null);
        var retryable = error is not WorkflowException { Code: "fieldNotSettable" or "unknownAction" or "missingInput" or "noRecord" or "goToInvalidTarget" or "noAssignees" or "webhookOriginNotAllowed" or "fieldOwned" };
        if (retry is { Max: > 0 } && attempt <= retry.Max && retryable)
        {
            var initial = retry.Initial is null ? TimeSpan.FromMinutes(1) : IsoDuration.Parse(retry.Initial).Approximate;
            var delay = retry.Backoff == "exponential" ? TimeSpan.FromTicks(initial.Ticks * (1L << Math.Min(attempt - 1, 20))) : initial;
            if (retry.MaxDelay is not null)
            {
                var cap = IsoDuration.Parse(retry.MaxDelay).Approximate;
                if (delay > cap) delay = cap;
            }
            t.Top.Attempt = attempt + 1;
            await AddTimerAsync(x, t, step, BookmarkPurposes.Retry, x.U.Now + delay, null, ct);
            Wait(t, "retry", null);
            await x.U.AuditAsync(StepAudit(x, step, "retryScheduled", new Dictionary<string, object?> { ["attempt"] = (double)(attempt + 1), ["at"] = Values.FormatInstant(x.U.Now + delay) }), ct);
            return;
        }
        var mode = step.OnError?.Mode ?? "stop";
        switch (mode)
        {
            case "continue":
                x.S.SetOutput(step.Id, "error", message);
                Advance(t);
                break;
            case "steps":
                x.S.SetOutput(step.Id, "error", message);
                t.Frames.Add(new Frame { List = $"{step.Id}/error" });
                break;
            default:
                await RaiseIncidentAsync(x, t, step, message, ct);
                break;
        }
    }

    internal async Task RaiseIncidentAsync(Exec x, Token t, Step? step, string message, CancellationToken ct)
    {
        Wait(t, "incident", null);
        t.IncidentError = message;
        t.IncidentStepId = step?.Id;
        x.I.Status = InstanceStatus.Incident;
        x.I.Error = message;
        x.I.ErrorStepId = step?.Id;
        await x.U.AuditAsync(new AuditEntry
        {
            Category = "instance", Action = "incident", SubjectType = "instance", SubjectId = x.I.Id, InstanceId = x.I.Id, DefinitionId = x.I.DefinitionId,
            Entity = x.I.Entity, EntityId = x.I.EntityId, Actor = x.Actor, CorrelationId = x.I.CorrelationId,
            Data = new Dictionary<string, object?> { ["step"] = step?.Id, ["error"] = message, ["token"] = t.Id },
        }, ct);
        x.U.Log?.Add("instance", "incident", x.I.Id, message);
        var owners = x.C.Definition.Settings;
        if (owners?.NotifyOwnersOnIncident == true && owners.Owners is { Count: > 0 })
        {
            var scope = x.Scope(t);
            var people = await RecipientsAsync(x, scope, owners.Owners, ct);
            var channel = Options.TaskNotificationChannel ?? Registry.Channels.FirstOrDefault(c => c.Key is "inApp" or "email")?.Key;
            if (channel is not null && people.Count > 0)
            {
                await QueueNotificationAsync(x.U, new WorkflowNotification(channel, people, $"Workflow needs attention: {x.C.Definition.Title}",
                    $"“{x.C.Definition.Title}” stopped at “{step?.Title ?? step?.Type}”: {message}", RecordLink(x.I.Entity, x.I.EntityId), x.I.Id, x.I.Entity, x.I.EntityId,
                    $"{x.I.Id}:incident:{step?.Id}:{t.Top.Attempt}"), ct);
            }
        }
    }
}
