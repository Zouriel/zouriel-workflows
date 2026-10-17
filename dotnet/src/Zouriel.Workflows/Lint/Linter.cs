using Zouriel.Workflows.Expressions;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Registry;
using Zouriel.Workflows.Settings;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Lint;

/// <summary>One problem in a definition. <c>Node</c> is the id of the step, state or transition it is on.</summary>
/// <param name="Code">Stable problem code, the same in every engine and editor (<c>spec/fixtures/lint</c>).</param>
/// <param name="Severity"><c>error</c> (blocks publishing) or <c>warning</c>.</param>
/// <param name="Node">The id of the step, state or transition the problem is on; null for the whole definition.</param>
/// <param name="Message">What is wrong, in plain words.</param>
public sealed record LintProblem(string Code, string Severity, string? Node, string Message);

/// <summary>What a definition is checked against.</summary>
/// <param name="Registry">What the app lets workflows read, watch, write and call.</param>
/// <param name="Settings">The global settings (allowed webhook origins, channels…).</param>
/// <param name="OtherPublished">The other published definitions (lifecycle conflicts, sub-workflows, owned fields).</param>
public sealed record LintContext(WorkflowRegistry Registry, GlobalSettings Settings, IReadOnlyList<WorkflowDefinition> OtherPublished);

/// <summary>Checks a definition (spec §13). Errors block publishing; warnings don't.</summary>
public static class Linter
{
    /// <summary>The label of each step type, used in messages and served to editors.</summary>
    public static readonly IReadOnlyDictionary<string, string> StepLabels = new Dictionary<string, string>
    {
        ["branch"] = "Branch", ["switch"] = "Switch", ["parallel"] = "Parallel paths", ["forEach"] = "For each",
        ["approval"] = "Approval", ["task"] = "Task", ["wait"] = "Wait", ["setFields"] = "Update record", ["createRecord"] = "Create record",
        ["setVariables"] = "Set variables", ["applyTransition"] = "Move lifecycle", ["notify"] = "Send notification",
        ["webhook"] = "Call webhook", ["action"] = "Run action", ["subWorkflow"] = "Run workflow", ["goTo"] = "Go back",
        ["end"] = "End",
    };

    /// <summary>The outputs each step type makes available as <c>steps.ID.*</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> StepOutputs = new Dictionary<string, string[]>
    {
        ["branch"] = ["path"], ["switch"] = ["case"], ["parallel"] = ["completed"], ["forEach"] = ["count"],
        ["approval"] = ["outcome", "decidedBy", "comments", "data"], ["task"] = ["outcome", "decidedBy", "comment", "data"],
        ["wait"] = ["timedOut", "data"], ["setFields"] = [], ["createRecord"] = ["id"], ["setVariables"] = [], ["applyTransition"] = ["applied"],
        ["notify"] = ["sent", "error"], ["webhook"] = ["status", "body", "error"], ["action"] = ["error"],
        ["subWorkflow"] = ["instanceId", "status", "outputs", "error"], ["goTo"] = ["count"], ["end"] = [],
    };

    /// <summary>Checks a definition and returns every problem found (empty when it is fine).</summary>
    /// <param name="def">The definition to check.</param>
    /// <param name="ctx">The registry, settings and other published workflows to check against.</param>
    public static IReadOnlyList<LintProblem> Lint(WorkflowDefinition def, LintContext ctx) => new Run(def, ctx).Go();

    private sealed class Run(WorkflowDefinition def, LintContext ctx)
    {
        private readonly List<LintProblem> _p = [];
        private readonly HashSet<string> _ids = [];
        private readonly Dictionary<string, Step> _allSteps = [];
        private readonly Engine.CompiledDefinition _compiled = new(def, def.Id, 0);
        private WorkflowRegistry R => ctx.Registry;

        private void Error(string code, string? node, string message) => _p.Add(new LintProblem(code, "error", node, message));
        private void Warn(string code, string? node, string message) => _p.Add(new LintProblem(code, "warning", node, message));

        private static string Name(Step s) => string.IsNullOrWhiteSpace(s.Title) ? StepLabels.GetValueOrDefault(s.Type, s.Type) : s.Title!;

        public IReadOnlyList<LintProblem> Go()
        {
            if (def.SpecVersion != 1) Error("specVersion", null, $"This engine reads spec version 1; the definition says {def.SpecVersion}.");
            if (string.IsNullOrWhiteSpace(def.Title)) Error("missingTitle", null, "Give the workflow a name.");
            var entity = def.Entity is null ? null : R.Entity(def.Entity);
            if (def.Entity is not null && entity is null) Error("unknownEntity", null, $"'{def.Entity}' is not a record type the app allows workflows to use.");
            foreach (var v in def.Variables ?? [])
            {
                CheckId(v.Id, v.Id);
                if (def.Variables!.Count(x => x.Key == v.Key) > 1) Error("duplicateKey", v.Id, $"Two variables are called '{v.Key}'.");
            }
            if (def.IsLifecycle) LintLifecycle(entity);
            else LintAutomation(entity);
            return _p;
        }

        private void CheckId(string id, string? node)
        {
            if (string.IsNullOrEmpty(id)) Error("missingId", node, "An item has no id.");
            else if (!_ids.Add(id)) Error("duplicateId", node, $"The id '{id}' is used twice.");
        }

        // ── Automations ───────────────────────────────────────────────────

        private void LintAutomation(EntityDescriptor? entity)
        {
            var t = def.Trigger;
            if (t is null)
            {
                Error("missingTrigger", null, "Choose what starts this workflow.");
                return;
            }
            // Schedules (without "for each record"), app-wide signals and manual buttons can run without a record.
            var needsEntity = t.Type switch
            {
                TriggerTypes.Schedule => t.ForEach is not null,
                TriggerTypes.Signal => R.Signal(t.Signal)?.Entity is not null,
                TriggerTypes.Manual => false,
                _ => true,
            };
            if (needsEntity && def.Entity is null) Error("missingEntity", null, "Choose which record type this workflow is about.");
            if (t.Type == TriggerTypes.Schedule && t.ForEach is null && def.Entity is not null)
                Warn("scheduleEntity", null, "A schedule without 'for each record' runs without a record; the record type is ignored.");
            switch (t.Type)
            {
                case TriggerTypes.Created or TriggerTypes.Deleted:
                    break;
                case TriggerTypes.FieldChanged:
                    var f = entity?.Field(t.Field);
                    if (string.IsNullOrEmpty(t.Field)) Error("missingField", null, "Choose which field to watch.");
                    else if (entity is not null && f is null) Error("unknownField", null, $"'{t.Field}' is not a field of {entity.Label}.");
                    else if (f is not null && !f.IsWatchable) Error("fieldNotWatchable", null, $"{f.Label} cannot be watched; the app has not allowed it.");
                    if (f?.Options is { } opts)
                    {
                        foreach (var v in (t.To ?? []).Concat(t.From ?? []))
                        {
                            var text = Json.Values.Text(Json.Values.FromJson(v));
                            if (!opts.Any(o => Json.Values.Text(o.Value) == text)) Error("unknownValue", null, $"'{text}' is not one of {f.Label}'s values.");
                        }
                    }
                    break;
                case TriggerTypes.StateEntered or TriggerTypes.TransitionApplied:
                    var lc = ctx.OtherPublished.FirstOrDefault(d => d.IsLifecycle && d.Key == t.Lifecycle);
                    if (lc is null) Error("unknownLifecycle", null, $"There is no published lifecycle called '{t.Lifecycle}'.");
                    else
                    {
                        if (lc.Entity != def.Entity) Error("lifecycleEntity", null, $"The lifecycle '{lc.Title}' is about a different record type.");
                        if (t.Type == TriggerTypes.StateEntered)
                        {
                            if (t.States is not { Count: > 0 }) Error("missingStates", null, "Choose which states start the workflow.");
                            foreach (var s in t.States ?? [])
                                if (lc.Lifecycle!.States.All(x => x.Key != s)) Error("unknownState", null, $"'{s}' is not a state of {lc.Title}.");
                        }
                        else
                        {
                            if (t.Transitions is not { Count: > 0 }) Error("missingTransitions", null, "Choose which transitions start the workflow.");
                            foreach (var s in t.Transitions ?? [])
                                if (lc.Lifecycle!.Transitions.All(x => x.Key != s)) Error("unknownTransition", null, $"'{s}' is not a transition of {lc.Title}.");
                        }
                    }
                    break;
                case TriggerTypes.DateReached:
                    var df = entity?.Field(t.Field);
                    if (df is null) Error("unknownField", null, "Choose the date field.");
                    else if (df.Type is not (FieldTypes.Date or FieldTypes.DateTime)) Error("notADate", null, $"{df.Label} is not a date.");
                    else if (!df.IsWatchable) Error("fieldNotWatchable", null, $"{df.Label} cannot be watched; the app has not allowed it.");
                    if (!IsoDuration.TryParse(t.Offset, out _)) Error("badDuration", null, $"'{t.Offset}' is not a valid time offset.");
                    break;
                case TriggerTypes.Schedule:
                    if (!Cron.TryParse(t.Cron, out _, out var cronError)) Error("badCron", null, cronError ?? "The schedule is not valid.");
                    if (t.ForEach is { Limit: < 1 or > 500 }) Error("badLimit", null, "Scheduled runs can cover 1 to 500 records.");
                    break;
                case TriggerTypes.Manual:
                    if (string.IsNullOrWhiteSpace(t.Label)) Error("missingLabel", null, "Give the button a label.");
                    CheckAssignees(t.Who, null, allowEmpty: true);
                    break;
                case TriggerTypes.Signal:
                    var sig = R.Signal(t.Signal);
                    if (sig is null) Error("unknownSignal", null, $"'{t.Signal}' is not an event the app sends.");
                    else if (sig.Entity is not null && sig.Entity != def.Entity) Error("signalEntity", null, $"'{sig.Label}' is about a different record type.");
                    break;
                default:
                    Error("unknownTrigger", null, $"'{t.Type}' is not a trigger this engine knows.");
                    break;
            }
            if (t.When is not null) CheckCondition(t.When, null, new Env(entity?.Key, null, [], false, t));
            if (def.Steps is null || def.Steps.Count == 0) Warn("emptyWorkflow", null, "The workflow has no steps yet.");
            // input.* is always allowed in automations: manual triggers and sub-workflow calls both pass input.
            LintSteps(def.Steps ?? [], new Env(entity?.Key, null, [], true, t), "root");
            CheckLoops(entity);
        }

        /// <summary>A workflow that writes a field its own trigger watches starts itself again unless it opts in.</summary>
        private void CheckLoops(EntityDescriptor? entity)
        {
            var t = def.Trigger;
            if (t is null || entity is null || t.Retrigger == true) return;
            foreach (var s in _allSteps.Values.OfType<SetFieldsStep>())
            {
                if (s.Target is not (null or "self")) continue;
                if (t.Type == TriggerTypes.FieldChanged && s.Values.Any(v => v.Field == t.Field))
                    Warn("triggerLoop", s.Id, $"'{Name(s)}' changes {t.Field}, which starts this workflow. It will not start itself again (turn on 're-trigger' to allow it).");
            }
        }

        // ── Lifecycles ────────────────────────────────────────────────────

        private void LintLifecycle(EntityDescriptor? entity)
        {
            var lc = def.Lifecycle;
            if (lc is null)
            {
                Error("missingLifecycle", null, "Add the states of the lifecycle.");
                return;
            }
            if (def.Entity is null) Error("missingEntity", null, "Choose which record type this lifecycle is for.");
            var field = entity?.Field(lc.Field);
            if (entity is not null && field is null) Error("unknownField", null, $"'{lc.Field}' is not a field of {entity.Label}.");
            else if (field is not null && !field.IsLifecycle) Error("notLifecycleField", null, $"{field.Label} cannot be controlled by a lifecycle; the app has not allowed it.");
            // Several lifecycles may share a field when "applies to" tells them apart (spec §6): at most one
            // without a condition (the fallback for every other record).
            foreach (var other in ctx.OtherPublished.Where(d => d.IsLifecycle && d.Id != def.Id && d.Entity == def.Entity && d.Lifecycle?.Field == lc.Field))
            {
                var theirs = other.Lifecycle!.AppliesWhen;
                if (lc.AppliesWhen is null && theirs is null)
                    Error("lifecycleConflict", null, $"'{other.Title}' already controls this field for every record. Give one of them an “Applies to” condition.");
                else if (lc.AppliesWhen is not null && theirs is not null && !ClearlyApart(lc.AppliesWhen, theirs))
                    Warn("lifecyclesMayOverlap", null, $"'{other.Title}' may apply to the same records; a record gets the lifecycle published first.");
            }
            if (lc.AppliesWhen is { } applies)
            {
                CheckCondition(applies, null, new Env(entity?.Key, null, [], false, null));
                foreach (var r in Conditions.Refs(applies))
                {
                    var parts = r.Split('.');
                    if (parts.Length != 2 || parts[0] != "entity")
                        Error("appliesWhenField", null, "“Applies to” can only read this record's own fields (not related records, the trigger or steps).");
                    else if (parts[1] == lc.Field)
                        Error("appliesWhenField", null, "“Applies to” cannot read the field the lifecycle controls.");
                }
            }

            if (lc.States.Count == 0) Error("noStates", null, "Add at least one state.");
            var initials = lc.States.Count(s => s.IsInitial);
            if (initials == 0) Error("noInitialState", null, "Mark one state as the starting state.");
            if (initials > 1) Error("multipleInitialStates", null, "Only one state can be the starting state.");
            foreach (var s in lc.States)
            {
                CheckId(s.Id, s.Id);
                if (string.IsNullOrWhiteSpace(s.Label)) Error("missingLabel", s.Id, "Give every state a name.");
                if (lc.States.Count(x => x.Key == s.Key) > 1) Error("duplicateKey", s.Id, $"Two states use the value '{s.Key}'.");
                if (field?.Options is { } opts && opts.All(o => Json.Values.Text(o.Value) != s.Key))
                    Error("unknownValue", s.Id, $"'{s.Key}' is not one of {field.Label}'s values.");
                var env = new Env(entity?.Key, null, [], false, null);
                // Entering a state by a transition with a form passes its answers on (input.*).
                if (s.OnEnter is not null) LintSteps(s.OnEnter, env with { InputAllowed = true }, $"onEnter:{s.Id}");
                if (s.OnExit is not null) LintSteps(s.OnExit, env, $"onExit:{s.Id}");
                if (s.Sla is not null)
                {
                    if (!IsoDuration.TryParse(s.Sla.Within, out _)) Error("badDuration", s.Id, $"'{s.Sla.Within}' is not a valid time limit.");
                    LintSteps(s.Sla.Steps, env, $"sla:{s.Id}");
                    if (s.IsTerminal) Warn("slaOnTerminal", s.Id, $"A time limit on the final state '{s.Label}' is never checked.");
                }
            }
            var stateIds = lc.States.Select(s => s.Id).ToHashSet();
            foreach (var t in lc.Transitions)
            {
                CheckId(t.Id, t.Id);
                if (string.IsNullOrWhiteSpace(t.Label)) Error("missingLabel", t.Id, "Give every transition a name.");
                if (lc.Transitions.Count(x => x.Key == t.Key) > 1) Error("duplicateKey", t.Id, $"Two transitions use the key '{t.Key}'.");
                if (!stateIds.Contains(t.To)) Error("unknownState", t.Id, $"'{t.Label}' goes to a state that does not exist.");
                if (!t.From.Any)
                {
                    if (t.From.States.Count == 0) Error("missingFrom", t.Id, $"Choose which states '{t.Label}' starts from.");
                    foreach (var f in t.From.States)
                        if (!stateIds.Contains(f)) Error("unknownState", t.Id, $"'{t.Label}' starts from a state that does not exist.");
                    if (t.From.States.Contains(t.To)) Warn("selfTransition", t.Id, $"'{t.Label}' starts and ends in the same state.");
                    foreach (var f in t.From.States)
                        if (lc.States.FirstOrDefault(s => s.Id == f) is { IsTerminal: true } term) Warn("fromTerminal", t.Id, $"'{t.Label}' starts from the final state '{term.Label}'.");
                }
                CheckAssignees(t.Who, t.Id, allowEmpty: true);
                if ((t.Who is null || t.Who.Count == 0) && t.Auto is null)
                    Warn("nobodyCanApply", t.Id, $"Nobody can apply '{t.Label}' by hand and it is not automatic; only other workflows can.");
                var env = new Env(entity?.Key, null, [], t.Input is not null, null);
                if (t.When is not null) CheckCondition(t.When, t.Id, env with { InputAllowed = false });
                if (t.Auto is { } auto)
                {
                    if (auto.After is not null && !IsoDuration.TryParse(auto.After, out _)) Error("badDuration", t.Id, $"'{auto.After}' is not a valid time.");
                    if (auto.After is null && auto.When is null) Error("badAuto", t.Id, $"Choose when '{t.Label}' happens automatically.");
                    if (auto.When is not null) CheckCondition(auto.When, t.Id, env with { InputAllowed = false });
                    if (t.Input is not null) Warn("autoWithInput", t.Id, $"'{t.Label}' asks for input but also happens automatically; automatic moves skip the form.");
                }
                if (t.Input?.Form is { } form && form["pages"] is null) Error("badForm", t.Id, $"The form of '{t.Label}' is not a form definition.");
                if (t.Steps is not null) LintSteps(t.Steps, env, $"transition:{t.Id}");
            }
            if (lc.Reset is { } reset)
            {
                // Problems about the reset have no node of their own; the editor shows them on the lifecycle.
                if (reset.To is { } back && !stateIds.Contains(back)) Error("unknownState", null, "The reset goes back to a state that does not exist.");
                else if (lc.States.FirstOrDefault(s => s.Id == (reset.To ?? lc.States.FirstOrDefault(x => x.IsInitial)?.Id)) is { IsTerminal: true } final)
                    Warn("resetToFinal", null, $"The reset goes back to the final state '{final.Label}', which ends the workflow.");
                CheckAssignees(reset.Who, null, allowEmpty: true);
                if (reset.Who is not { Count: > 0 }) Warn("resetNobody", null, "Nobody can reset this workflow yet; choose who may.");
                if (reset.States is { } offeredIn)
                {
                    foreach (var st in offeredIn)
                        if (!stateIds.Contains(st)) Error("unknownState", null, $"The reset is offered in a state that does not exist ('{st}').");
                    if (offeredIn.Count == 0) Warn("resetNeverOffered", null, "The reset is offered in no state, so nobody can ever use it.");
                }
                if (reset.Input?.Form is { } resetForm && resetForm["pages"] is null) Error("badForm", null, "The reset's form is not a form definition.");
                if (reset.Steps is not null) LintSteps(reset.Steps, new Env(entity?.Key, null, [], reset.Input is not null, null), "reset");
            }
            // Reachability from the initial state.
            var initial = lc.States.FirstOrDefault(s => s.IsInitial);
            if (initial is not null)
            {
                var seen = new HashSet<string> { initial.Id };
                var queue = new Queue<string>([initial.Id]);
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var t in lc.Transitions.Where(t => t.From.Allows(lc, cur, t.To)))
                        if (seen.Add(t.To)) queue.Enqueue(t.To);
                }
                foreach (var s in lc.States.Where(s => !seen.Contains(s.Id)))
                    Warn("unreachableState", s.Id, $"No transition leads to '{s.Label}'.");
            }
            foreach (var s in lc.States.Where(s => !s.IsTerminal))
                if (!lc.Transitions.Any(t => t.From.Allows(lc, s.Id, t.To)))
                    Warn("deadEndState", s.Id, $"'{s.Label}' has no way out. Mark it as a final state or add a transition.");
            if (lc.States.Count > 0 && !lc.States.Any(s => s.IsTerminal)) Warn("noTerminalState", null, "No state is marked as final, so records never finish.");
        }

        // ── Steps ─────────────────────────────────────────────────────────

        /// <param name="Entity">The record type <c>entity.*</c> reads.</param>
        /// <param name="ItemEntity">Inside forEach: the record type <c>item.*</c> reads.</param>
        /// <param name="Upstream">Steps whose outputs are available.</param>
        private sealed record Env(string? Entity, string? ItemEntity, List<Step> Upstream, bool InputAllowed, Trigger? Trigger)
        {
            public bool InParallelOrLoop { get; init; }
            public List<string> EnclosingLists { get; init; } = [];
        }

        private void LintSteps(List<Step> steps, Env env, string listId)
        {
            var upstream = new List<Step>(env.Upstream);
            var lists = new List<string>(env.EnclosingLists) { listId };
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                CheckId(s.Id, s.Id);
                _allSteps.TryAdd(s.Id, s);
                var here = env with { Upstream = upstream, EnclosingLists = lists };
                LintStep(s, here, listId, i);
                if (s is NotifyStep && (s.Retry is not null || s.OnError is not null))
                    Warn("notifyDelivery", s.Id, "Notifications are delivered after the step through the outbox, which retries on its own; retry and on-error settings here have no effect.");
                if (s.Retry is { } r)
                {
                    if (r.Max is < 0 or > 10) Error("badRetry", s.Id, "Retries can be 0 to 10.");
                    if (r.Initial is not null && !IsoDuration.TryParse(r.Initial, out _)) Error("badDuration", s.Id, $"'{r.Initial}' is not a valid delay.");
                    if (r.MaxDelay is not null && !IsoDuration.TryParse(r.MaxDelay, out _)) Error("badDuration", s.Id, $"'{r.MaxDelay}' is not a valid delay.");
                }
                if (s.Timeout is not null && !IsoDuration.TryParse(s.Timeout, out _)) Error("badDuration", s.Id, $"'{s.Timeout}' is not a valid time limit.");
                if (s.OnError is { Mode: "steps", Steps: { } es }) LintSteps(es, here with { Upstream = [.. upstream, s] }, $"{s.Id}/error");
                upstream.Add(s);
            }
        }

        private Env Inside(Env env, Step container) => env with { Upstream = [.. env.Upstream, container] };

        private void LintStep(Step s, Env env, string listId, int index)
        {
            switch (s)
            {
                case UnknownStep:
                    Error("unknownStepType", s.Id, $"'{s.Type}' is not a step this engine knows.");
                    break;
                case BranchStep b: LintBranch(b, env); break;
                case SwitchStep sw: LintSwitch(sw, env); break;
                case ParallelStep par: LintParallel(par, env); break;
                case ForEachStep fe: LintForEach(fe, env); break;
                case ApprovalStep ap: LintApproval(ap, env); break;
                case TaskStep tk: LintTask(tk, env); break;
                case WaitStep w: LintWait(w, env); break;
                case SetFieldsStep sf: LintSetFields(sf, env); break;
                case CreateRecordStep cr: LintCreateRecord(cr, env); break;
                case SetVariablesStep sv: LintSetVariables(sv, env); break;
                case ApplyTransitionStep at: LintApplyTransition(at, env); break;
                case NotifyStep n: LintNotify(n, env); break;
                case WebhookStep wh: LintWebhook(wh, env); break;
                case ActionStep act: LintAction(act, env); break;
                case SubWorkflowStep sub: LintSubWorkflow(sub, env); break;
                case GoToStep g: LintGoTo(g, env, listId, index); break;
                case EndStep e: CheckTemplate(e.Reason, e.Id, env); break;
            }
        }

        private void LintBranch(BranchStep b, Env env)
        {
            if (b.Paths.Count == 0) Error("emptyBranch", b.Id, $"'{Name(b)}' has no paths.");
            foreach (var p in b.Paths)
            {
                CheckId(p.Id, b.Id);
                if (p.When is null) Error("missingCondition", b.Id, $"Path '{p.Label}' of '{Name(b)}' needs a condition.");
                else CheckCondition(p.When, b.Id, env);
                LintSteps(p.Steps, Inside(env, b), $"{b.Id}/path:{p.Id}");
            }
            if (b.Otherwise is not null)
            {
                CheckId(b.Otherwise.Id, b.Id);
                LintSteps(b.Otherwise.Steps, Inside(env, b), $"{b.Id}/otherwise");
            }
        }

        private void LintSwitch(SwitchStep sw, Env env)
        {
            CheckToken(sw.On, sw.Id, env);
            if (sw.Cases.Count == 0) Error("emptySwitch", sw.Id, $"'{Name(sw)}' has no cases.");
            foreach (var c in sw.Cases)
            {
                CheckId(c.Id, sw.Id);
                if (c.Values.Count == 0) Error("missingValues", sw.Id, $"Case '{c.Label}' of '{Name(sw)}' needs at least one value.");
                LintSteps(c.Steps, Inside(env, sw), $"{sw.Id}/case:{c.Id}");
            }
            if (sw.Otherwise is not null)
            {
                CheckId(sw.Otherwise.Id, sw.Id);
                LintSteps(sw.Otherwise.Steps, Inside(env, sw), $"{sw.Id}/otherwise");
            }
        }

        private void LintParallel(ParallelStep par, Env env)
        {
            if (par.Paths.Count < 2) Error("parallelPaths", par.Id, $"'{Name(par)}' needs at least two paths.");
            if (par.Join.Type == "count" && (par.Join.Count is null || par.Join.Count < 1 || par.Join.Count > par.Paths.Count))
                Error("parallelJoinCount", par.Id, $"'{Name(par)}' waits for {par.Join.Count} paths but has {par.Paths.Count}.");
            foreach (var p in par.Paths)
            {
                CheckId(p.Id, par.Id);
                LintSteps(p.Steps, Inside(env, par) with { InParallelOrLoop = true, EnclosingLists = [] }, $"{par.Id}/path:{p.Id}");
            }
        }

        private void LintForEach(ForEachStep fe, Env env)
        {
            var collectionEntity = CollectionEntity(fe.Collection, env, fe.Id);
            if (fe.Max is < 1 or > 500) Error("badLimit", fe.Id, "For each can cover 1 to 500 items.");
            var loopEnv = Inside(env, fe) with { ItemEntity = collectionEntity, InParallelOrLoop = true, EnclosingLists = [] };
            if (fe.When is not null) CheckCondition(fe.When, fe.Id, loopEnv);
            LintSteps(fe.Steps, loopEnv, $"{fe.Id}/each");
        }

        private void LintApproval(ApprovalStep ap, Env env)
        {
            LintHuman(ap, env);
            if (ap.Rule.Type is "count" or "percent" && (ap.Rule.Value is null || ap.Rule.Value < 1))
                Error("badRule", ap.Id, $"'{Name(ap)}' needs a number of approvals.");
            if (ap.Rule.Type == "percent" && ap.Rule.Value > 100) Error("badRule", ap.Id, "A percentage cannot be over 100.");
            if (ap.Outcomes.TimedOut.Count == 0)
                Warn("emptyTimeoutPath", ap.Id, $"Nothing happens when '{Name(ap)}' runs out of time; the workflow just carries on.");
            LintSteps(ap.Outcomes.Approved, Inside(env, ap), $"{ap.Id}/outcome:approved");
            LintSteps(ap.Outcomes.Rejected, Inside(env, ap), $"{ap.Id}/outcome:rejected");
            LintSteps(ap.Outcomes.TimedOut, Inside(env, ap), $"{ap.Id}/outcome:timedOut");
        }

        private void LintTask(TaskStep tk, Env env)
        {
            LintHuman(tk, env);
            if (tk.Outcomes.Count == 0) Error("noOutcomes", tk.Id, $"'{Name(tk)}' needs at least one button (outcome).");
            foreach (var o in tk.Outcomes)
            {
                CheckId(o.Id, tk.Id);
                if (tk.Outcomes.Count(x => x.Key == o.Key) > 1) Error("duplicateKey", tk.Id, $"Two outcomes of '{Name(tk)}' use the key '{o.Key}'.");
                LintSteps(o.Steps, Inside(env, tk), $"{tk.Id}/outcome:{o.Key}");
            }
            if (tk.RequireComment == "reject" && !tk.Outcomes.Any(o => o.Tone == "danger" || o.Key == "rejected"))
                Warn("noNegativeOutcome", tk.Id, $"'{Name(tk)}' asks for a comment on rejecting, but none of its buttons is red (danger), so it never does.");
            CheckId(tk.OnTimeout.Id, tk.Id);
            LintSteps(tk.OnTimeout.Steps, Inside(env, tk), $"{tk.Id}/onTimeout");
        }

        private void LintSetFields(SetFieldsStep sf, Env env)
        {
            var target = TargetEntity(sf.Target, env, sf.Id);
            if (sf.Values.Count == 0) Error("noValues", sf.Id, $"'{Name(sf)}' does not change anything yet.");
            foreach (var v in sf.Values)
            {
                var f = R.Field(target, v.Field);
                if (target is not null && f is null) Error("unknownField", sf.Id, $"'{v.Field}' is not a field of {R.Entity(target)?.Label}.");
                else if (f is not null && !f.IsSettable) Error("fieldNotSettable", sf.Id, $"Workflows cannot change {f.Label}; the app has not allowed it.");
                else if (f is not null && v.Value.HasConst && f.Options is { } opts && v.Value.Const is not null
                         && opts.All(o => Json.Values.Text(o.Value) != Json.Values.Text(v.Value.Const)))
                    Error("unknownValue", sf.Id, $"'{Json.Values.Text(v.Value.Const)}' is not one of {f.Label}'s values.");
                if (f is not null && def.IsLifecycle && def.Lifecycle?.Field == f.Key && target == def.Entity)
                    Error("ownedField", sf.Id, $"{f.Label} is controlled by this lifecycle; use 'Move lifecycle' instead.");
                var owner = ctx.OtherPublished.FirstOrDefault(d => d.IsLifecycle && d.Entity == target && d.Lifecycle?.Field == v.Field && d.Lifecycle.Enforce);
                if (owner is not null) Error("ownedField", sf.Id, $"{f?.Label ?? v.Field} is controlled by the lifecycle '{owner.Title}'; use 'Move lifecycle' instead.");
                CheckOperand(v.Value, sf.Id, env);
            }
        }

        private void LintCreateRecord(CreateRecordStep cr, Env env)
        {
            var target = R.Entity(cr.Entity);
            if (target is null)
            {
                Error(string.IsNullOrEmpty(cr.Entity) ? "missingEntity" : "unknownEntity", cr.Id, $"Choose which kind of record '{Name(cr)}' creates.");
                return;
            }
            // Fields a lifecycle controls (this one or a published one): a new record starts in the lifecycle's first state.
            bool Owned(string field) => (def.IsLifecycle && def.Entity == target.Key && def.Lifecycle?.Field == field)
                                        || ctx.OtherPublished.Any(d => d.IsLifecycle && d.Entity == target.Key && d.Lifecycle?.Field == field);
            foreach (var v in cr.Values)
            {
                var f = target.Field(v.Field);
                if (f is null) Error("unknownField", cr.Id, $"'{v.Field}' is not a field of {target.Label}.");
                else if (!f.IsSettable) Error("fieldNotSettable", cr.Id, $"Workflows cannot set {f.Label}; the app has not allowed it.");
                else if (Owned(f.Key))
                    Error("ownedField", cr.Id, $"{f.Label} is controlled by a lifecycle; a new record starts in its first state.");
                else if (v.Value.HasConst && f.Options is { } opts && v.Value.Const is not null
                         && opts.All(o => Json.Values.Text(o.Value) != Json.Values.Text(v.Value.Const)))
                    Error("unknownValue", cr.Id, $"'{Json.Values.Text(v.Value.Const)}' is not one of {f.Label}'s values.");
                CheckOperand(v.Value, cr.Id, env);
            }
            // Fields the app requires must be given (a lifecycle's field is filled with its first state).
            foreach (var f in target.Fields.Where(f => f.Required == true && f.Type != FieldTypes.Collection && !Owned(f.Key) && cr.Values.All(v => v.Field != f.Key)))
                Error("missingRequiredField", cr.Id, $"'{Name(cr)}' must set {f.Label}; the app requires it.");
        }

        private void LintSetVariables(SetVariablesStep sv, Env env)
        {
            foreach (var v in sv.Values)
            {
                if (def.Variables?.Any(x => x.Id == v.Variable) != true) Error("unknownVariable", sv.Id, $"'{Name(sv)}' sets a variable that does not exist.");
                CheckOperand(v.Value, sv.Id, env);
            }
        }

        private void LintApplyTransition(ApplyTransitionStep at, Env env)
        {
            var atEntity = TargetEntity(at.Target, env, at.Id);
            var lcDef = def.IsLifecycle && def.Key == at.Lifecycle ? def : ctx.OtherPublished.FirstOrDefault(d => d.IsLifecycle && d.Key == at.Lifecycle);
            if (lcDef is null) Error("unknownLifecycle", at.Id, $"There is no published lifecycle called '{at.Lifecycle}'.");
            else
            {
                if (lcDef.Entity != atEntity) Error("lifecycleEntity", at.Id, $"The lifecycle '{lcDef.Title}' is for a different record type.");
                if (lcDef.Lifecycle!.Transitions.All(t => t.Key != at.Transition)) Error("unknownTransition", at.Id, $"'{at.Transition}' is not a transition of {lcDef.Title}.");
            }
            foreach (var v in at.Input?.Values ?? Enumerable.Empty<Operand>()) CheckOperand(v, at.Id, env);
            CheckTemplate(at.Comment, at.Id, env);
        }

        private void LintNotify(NotifyStep n, Env env)
        {
            if (R.Channels.All(c => c.Key != n.Channel)) Error("unknownChannel", n.Id, $"'{n.Channel}' is not a notification channel the app offers.");
            else if (ctx.Settings.Channels?.GetValueOrDefault(n.Channel)?.Enabled == false) Warn("channelDisabled", n.Id, $"The {n.Channel} channel is switched off in settings.");
            if (n.To.Count == 0) Error("noRecipients", n.Id, $"'{Name(n)}' has nobody to send to.");
            CheckAssignees(n.To, n.Id, allowEmpty: true, env);
            CheckTemplate(n.Subject, n.Id, env);
            CheckTemplate(n.Body, n.Id, env);
            if (n.Body.Parts.Count == 0) Error("emptyMessage", n.Id, $"'{Name(n)}' has no message.");
        }

        private void LintAction(ActionStep act, Env env)
        {
            var desc = R.Action(act.Action);
            if (desc is null) Error("unknownAction", act.Id, $"'{act.Action}' is not an action the app offers.");
            else
            {
                if (desc.Entity is not null && desc.Entity != env.Entity) Error("actionEntity", act.Id, $"'{desc.Label}' works on a different record type.");
                foreach (var i in desc.Inputs.Where(i => i.Required == true))
                    if (act.Input?.ContainsKey(i.Key) != true) Error("missingInput", act.Id, $"'{Name(act)}' needs a value for {i.Label}.");
                foreach (var k in act.Input?.Keys ?? Enumerable.Empty<string>())
                    if (desc.Inputs.All(i => i.Key != k)) Warn("unknownInput", act.Id, $"'{desc.Label}' has no input called '{k}'.");
            }
            foreach (var v in act.Input?.Values ?? Enumerable.Empty<Operand>()) CheckOperand(v, act.Id, env);
        }

        private void LintSubWorkflow(SubWorkflowStep sub, Env env)
        {
            if (sub.Workflow == def.Key) Error("selfSubWorkflow", sub.Id, "A workflow cannot run itself.");
            else
            {
                var child = ctx.OtherPublished.FirstOrDefault(d => d.Key == sub.Workflow);
                if (child is null) Error("unknownWorkflow", sub.Id, $"There is no published workflow called '{sub.Workflow}'.");
                else if (child.IsLifecycle) Error("notAutomation", sub.Id, $"'{child.Title}' is a lifecycle; only automations can be run as steps.");
                else if (child.Entity is not null && child.Entity != TargetEntity(sub.Target, env, sub.Id))
                    Error("workflowEntity", sub.Id, $"'{child.Title}' is about a different record type.");
                else if (CycleThrough(sub.Workflow) is { } path)
                    Error("subWorkflowCycle", sub.Id, $"'{Name(sub)}' runs '{child.Title}', which leads back to this workflow ({string.Join(" → ", path)}). Workflows that run each other never stop; use 'Go back' for repeats.");
            }
            foreach (var v in sub.Input?.Values ?? Enumerable.Empty<Operand>()) CheckOperand(v, sub.Id, env);
        }

        private Dictionary<string, List<string>>? _subGraph;

        /// <summary>
        /// The chain of workflow keys from <paramref name="start"/> back to this definition through sub-workflow steps of
        /// the published workflows (this definition in place of its own published version), or null when there is none.
        /// </summary>
        private List<string>? CycleThrough(string start)
        {
            static IEnumerable<string> Calls(Engine.CompiledDefinition c) =>
                c.Lists.Values.SelectMany(l => l).OfType<SubWorkflowStep>().Select(s => s.Workflow).Distinct();
            _subGraph ??= ctx.OtherPublished.Where(d => d.Id != def.Id && !d.IsLifecycle)
                .Select(d => (d.Key, Calls: Calls(new Engine.CompiledDefinition(d, d.Id, 0)).ToList()))
                .Append((def.Key, Calls: Calls(_compiled).ToList()))
                .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Last().Calls);
            var seen = new HashSet<string>();
            List<string>? Walk(string key, List<string> trail)
            {
                if (key == def.Key) return [def.Key, .. trail];
                if (!seen.Add(key) || !_subGraph.TryGetValue(key, out var next)) return null;
                foreach (var n in next)
                    if (Walk(n, [.. trail, n]) is { } found) return found;
                return null;
            }
            return Walk(start, [start]);
        }

        private void LintGoTo(GoToStep g, Env env, string listId, int index)
        {
            if (g.MaxTimes is < 1 or > 50) Error("badLimit", g.Id, "Go back can repeat 1 to 50 times.");
            // Valid targets come before the current position, in this list or an enclosing one.
            var reachable = env.EnclosingLists.Any(list => FindIndex(list, g.Target) is { } idx && (list != listId || idx < index));
            if (!reachable) Error("goToInvalidTarget", g.Id, $"'{Name(g)}' must go back to an earlier step on the same path.");
        }

        private int? FindIndex(string listId, string stepId)
        {
            if (!_compiled.Lists.TryGetValue(listId, out var list)) return null;
            var i = list.FindIndex(x => x.Id == stepId);
            return i < 0 ? null : i;
        }

        private void LintHuman(HumanStep h, Env env)
        {
            CheckAssignees(h.Assignees, h.Id, allowEmpty: false, env);
            if (!IsoDuration.TryParse(h.Due, out var due) || due.Negative || due.IsZero) Error("badDuration", h.Id, $"'{Name(h)}' needs a due time.");
            foreach (var r in h.Reminders ?? [])
            {
                if (!IsoDuration.TryParse(r, out var rd)) Error("badDuration", h.Id, $"'{r}' is not a valid reminder time.");
                else if (rd.Approximate >= due.Approximate) Warn("lateReminder", h.Id, $"A reminder of '{Name(h)}' is set after it is due.");
            }
            if (h.Escalation is { } esc)
            {
                if (!IsoDuration.TryParse(esc.After, out _)) Error("badDuration", h.Id, $"'{esc.After}' is not a valid escalation time.");
                CheckAssignees(esc.Assignees, h.Id, allowEmpty: false, env);
            }
            CheckTemplate(h.Instructions, h.Id, env);
            if (h.Form is { } form && form["pages"] is null) Error("badForm", h.Id, $"The form of '{Name(h)}' is not a form definition.");
        }

        private void LintWait(WaitStep w, Env env)
        {
            var u = w.Until;
            switch (u.Type)
            {
                case "duration":
                    if (!IsoDuration.TryParse(u.Duration, out var d) || d.Negative) Error("badDuration", w.Id, $"'{Name(w)}' needs a length of time.");
                    break;
                case "date":
                    if (u.Date is null) Error("missingDate", w.Id, $"'{Name(w)}' needs a date.");
                    else CheckOperand(u.Date, w.Id, env);
                    if (u.Offset is not null && !IsoDuration.TryParse(u.Offset, out _)) Error("badDuration", w.Id, $"'{u.Offset}' is not a valid offset.");
                    break;
                case "condition":
                    if (u.When is null) Error("missingCondition", w.Id, $"'{Name(w)}' needs a condition.");
                    else CheckCondition(u.When, w.Id, env);
                    if (!IsoDuration.TryParse(u.Timeout, out _)) Error("badDuration", w.Id, $"'{Name(w)}' needs a time limit.");
                    if (env.Entity is null) Error("missingEntity", w.Id, $"'{Name(w)}' waits on the record, but this workflow has no record.");
                    break;
                case "signal":
                    var sig = R.Signal(u.Signal);
                    if (sig is null) Error("unknownSignal", w.Id, $"'{u.Signal}' is not an event the app sends.");
                    if (u.Timeout is not null && !IsoDuration.TryParse(u.Timeout, out _)) Error("badDuration", w.Id, $"'{u.Timeout}' is not a valid time limit.");
                    break;
                default:
                    Error("badWait", w.Id, $"'{Name(w)}' has an unknown kind of wait.");
                    break;
            }
            if (u.OnTimeout is not null)
            {
                CheckId(u.OnTimeout.Id, w.Id);
                LintSteps(u.OnTimeout.Steps, Inside(env, w), $"{w.Id}/onTimeout");
            }
        }

        private void LintWebhook(WebhookStep wh, Env env)
        {
            if (wh.Connection is not null)
            {
                if (ctx.Settings.Webhooks?.Connections?.Any(c => c.Key == wh.Connection) != true)
                    Error("unknownConnection", wh.Id, $"There is no connection called '{wh.Connection}' in settings.");
            }
            else if (string.IsNullOrWhiteSpace(wh.Url) || !Uri.TryCreate(wh.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                Error("badUrl", wh.Id, $"'{Name(wh)}' needs a web address (or a connection from settings).");
            }
            else
            {
                var origin = $"{uri.Scheme}://{uri.Authority}";
                if (ctx.Settings.Webhooks?.AllowedOrigins?.Contains(origin, StringComparer.OrdinalIgnoreCase) != true)
                    Error("webhookOriginNotAllowed", wh.Id, $"{origin} is not in the allowed addresses in settings.");
            }
            CheckTemplate(wh.Path, wh.Id, env);
            foreach (var h in wh.Headers?.Values ?? Enumerable.Empty<Template>()) CheckTemplate(h, wh.Id, env);
            foreach (var r in BodyRefs(wh.Body)) CheckToken(r, wh.Id, env);
        }

        internal static IEnumerable<string> BodyRefs(System.Text.Json.Nodes.JsonNode? n)
        {
            switch (n)
            {
                case System.Text.Json.Nodes.JsonObject o when o.Count == 1 && o["ref"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var path):
                    yield return path;
                    break;
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var kv in o)
                    foreach (var r in BodyRefs(kv.Value)) yield return r;
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var x in a)
                    foreach (var r in BodyRefs(x)) yield return r;
                    break;
            }
        }

        // ── Shared checks ─────────────────────────────────────────────────

        private void CheckAssignees(List<Assignee>? list, string? node, bool allowEmpty, Env? env = null)
        {
            if (list is null || list.Count == 0)
            {
                if (!allowEmpty) Error("noAssignees", node, "Choose who this is for.");
                return;
            }
            foreach (var a in list)
            {
                if (a.Role is not null && R.Roles.Count > 0 && R.Roles.All(r => r.Key != a.Role)) Error("unknownRole", node, $"'{a.Role}' is not a role the app has.");
                if (a.Group is not null && R.Groups.Count > 0 && R.Groups.All(g => g.Key != a.Group)) Error("unknownGroup", node, $"'{a.Group}' is not a group the app has.");
                if (a.Field is not null)
                {
                    if (env is not null) CheckToken(a.Field, node, env, expectUser: true);
                    else if (!a.Field.StartsWith("entity.", StringComparison.Ordinal)) Error("unknownToken", node, $"'{a.Field}' is not a person on the record.");
                    // Transitions, the reset and manual starts name people on the record: the field must exist and hold a person.
                    else CheckToken(a.Field, node, new Env(def.Entity, null, [], false, null), expectUser: true);
                }
                if (a.ManagerOf is not null and not "initiator" and not "assignees" && env is not null) CheckToken(a.ManagerOf, node, env, expectUser: true);
                if (a.Email is not null) CheckTemplate(a.Email, node, env ?? new Env(def.Entity, null, [], true, def.Trigger));
            }
        }

        /// <summary>
        /// Whether two "applies to" conditions can never hold for the same record: both require the same field
        /// to equal constants that "is" (<c>eq</c>) treats as different (the common "Type is Passport" / "Type is Visa" case).
        /// </summary>
        private static bool ClearlyApart(Condition a, Condition b)
        {
            static Dictionary<string, object?> Required(Condition c)
            {
                var rules = c switch
                {
                    ConditionRule r => [r],
                    ConditionGroup { Match: "all" } g => g.Conditions.OfType<ConditionRule>().ToList(),
                    _ => new List<ConditionRule>(),
                };
                return rules.Where(r => r.Op == "eq" && r.Value is { HasConst: true }).GroupBy(r => r.Ref).ToDictionary(g => g.Key, g => g.First().Value!.Const);
            }
            var ra = Required(a);
            // Compared the way "is" compares (case, spaces, numbers and yes/no alike), so "passport" and "Passport" are not apart.
            return Required(b).Any(kv => ra.TryGetValue(kv.Key, out var v) && !Conditions.Same(Json.Values.Normalize(v), Json.Values.Normalize(kv.Value)));
        }

        private void CheckCondition(Condition c, string? node, Env env)
        {
            if (!Conditions.IsComplete(c)) Error("incompleteCondition", node, "A condition is not finished.");
            foreach (var r in Conditions.Refs(c)) CheckToken(r, node, env);
            if (c is ConditionRule or ConditionGroup) CheckOps(c, node);
        }

        private void CheckOps(Condition c, string? node)
        {
            switch (c)
            {
                case ConditionGroup g:
                    if (g.Match is not ("all" or "any" or "none")) Error("badCondition", node, $"'{g.Match}' is not a way to combine conditions.");
                    foreach (var x in g.Conditions) CheckOps(x, node);
                    break;
                case ConditionRule r:
                    if (r.Op is "visible" or "hidden" or "complete" or "incomplete" or "rowsGte" or "rowsLte")
                        Error("formOnlyOperator", node, $"'{r.Op}' only works in forms.");
                    break;
            }
        }

        private void CheckOperand(Operand o, string? node, Env env)
        {
            if (o.Ref is not null) CheckToken(o.Ref, node, env);
            if (o.Calc is not null)
                foreach (var r in Conditions.CalcRefs(o.Calc)) CheckToken(r, node, env);
        }

        private void CheckTemplate(Template? t, string? node, Env env)
        {
            if (t is null) return;
            foreach (var p in t.Parts.OfType<TemplateRef>()) CheckToken(p.Ref, node, env);
        }

        private string? TargetEntity(string? target, Env env, string node)
        {
            if (target is null or "self") return env.Entity;
            if (target == "item")
            {
                if (env.ItemEntity is null) Error("badTarget", node, "'item' can only be changed inside 'For each'.");
                return env.ItemEntity;
            }
            var f = FieldAt(target, env);
            if (f is null || f.Type != FieldTypes.Relation)
            {
                Error("badTarget", node, $"'{target}' is not a related record.");
                return null;
            }
            return f.Entity;
        }

        private string? CollectionEntity(string path, Env env, string node)
        {
            var f = FieldAt(path, env);
            if (f is null || f.Type != FieldTypes.Collection)
            {
                Error("notACollection", node, $"'{path}' is not a list of related records.");
                return null;
            }
            return f.Entity;
        }

        /// <summary>The registered field an <c>entity.*</c>/<c>item.*</c> path ends at.</summary>
        private FieldDescriptor? FieldAt(string path, Env env)
        {
            var parts = path.Split('.');
            var entity = parts[0] switch { "entity" => env.Entity, "item" => env.ItemEntity, _ => null };
            FieldDescriptor? f = null;
            for (var i = 1; i < parts.Length && entity is not null; i++)
            {
                f = R.Field(entity, parts[i]);
                if (f is null || !f.IsReadable) return null;
                entity = f.Type is FieldTypes.Relation or FieldTypes.Collection ? f.Entity : null;
                if (entity is null && i < parts.Length - 1) return f.Type == FieldTypes.Json ? f : null;
            }
            return f;
        }

        private void CheckToken(string path, string? node, Env env, bool expectUser = false)
        {
            var parts = path.Split('.');
            switch (parts[0])
            {
                case "entity" or "item":
                    if (parts[0] == "entity" && env.Entity is null) { Error("unknownToken", node, $"'{path}' reads the record, but this workflow has no record."); return; }
                    if (parts[0] == "item" && env.ItemEntity is null) { Error("unknownToken", node, $"'{path}' can only be used inside 'For each'."); return; }
                    if (parts.Length == 2 && parts[1] is "id" or "index") return;
                    var f = FieldAt(path, env);
                    if (f is null) Error("unknownToken", node, $"'{path}' is not a field the app allows workflows to read.");
                    else if (expectUser && f.Type != FieldTypes.User) Error("notAUser", node, $"{f.Label} does not hold a person.");
                    return;
                case "trigger":
                    if (parts.Length < 2 || parts[1] is not ("field" or "from" or "to" or "actor" or "actorName" or "at" or "transition" or "data" or "type" or "comment"))
                        Error("unknownToken", node, $"'{path}' is not something the trigger provides.");
                    if (expectUser && parts.ElementAtOrDefault(1) != "actor") Error("notAUser", node, $"'{path}' does not hold a person.");
                    return;
                case "input":
                    if (!env.InputAllowed) Error("unknownToken", node, $"'{path}' reads a form, but there is no form here.");
                    return;
                case "steps":
                    if (parts.Length < 3) { Error("unknownToken", node, $"'{path}' is incomplete."); return; }
                    var up = env.Upstream.FirstOrDefault(s => s.Id == parts[1]);
                    if (up is null)
                    {
                        Error(_compiled.Steps.ContainsKey(parts[1]) ? "tokenNotUpstream" : "unknownToken", node,
                            $"'{path}' reads a step that may not have run yet at this point.");
                        return;
                    }
                    var outputs = up is ActionStep a && R.Action(a.Action) is { } ad
                        ? ad.Outputs.Select(o => o.Key).Append("error").ToArray()
                        : StepOutputs.GetValueOrDefault(up.Type, []);
                    if (!outputs.Contains(parts[2])) Error("unknownToken", node, $"'{Name(up)}' has no output called '{parts[2]}'.");
                    return;
                case "vars":
                    if (parts.Length < 2 || def.Variables?.Any(v => v.Key == parts[1]) != true) Error("unknownToken", node, $"'{path}' is not a variable of this workflow.");
                    return;
                case "instance":
                    if (parts.Length != 2 || parts[1] is not ("id" or "startedAt" or "initiator")) Error("unknownToken", node, $"'{path}' is not something the instance provides.");
                    return;
                case "now" or "today":
                    if (parts.Length != 1) Error("unknownToken", node, $"'{path}' is not valid.");
                    return;
                default:
                    Error("unknownToken", node, $"'{path}' is not a value workflows can read.");
                    return;
            }
        }
    }
}
