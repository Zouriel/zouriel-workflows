using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Lint;

/// <summary>Plain-English differences between two versions of a workflow, shown before publishing and stored in the audit log.</summary>
public static class DefinitionDiff
{
    /// <summary>One sentence per change from <paramref name="before"/> to <paramref name="after"/> (empty when they match).</summary>
    /// <param name="before">The earlier version (usually the published one).</param>
    /// <param name="after">The later version (usually the draft).</param>
    public static IReadOnlyList<string> Describe(WorkflowDefinition before, WorkflowDefinition after)
    {
        var lines = new List<string>();
        if (before.Title != after.Title) lines.Add($"Renamed from “{before.Title}” to “{after.Title}”.");
        if (Definitions.Serialize(Wrap(before.Trigger)) != Definitions.Serialize(Wrap(after.Trigger))) lines.Add("Changed what starts the workflow.");

        var a = new CompiledDefinition(before, before.Id, 0).Steps;
        var b = new CompiledDefinition(after, after.Id, 0).Steps;
        foreach (var (id, loc) in b.Where(kv => !a.ContainsKey(kv.Key))) lines.Add($"Added step “{Label(loc.Step)}”.");
        foreach (var (id, loc) in a.Where(kv => !b.ContainsKey(kv.Key))) lines.Add($"Removed step “{Label(loc.Step)}”.");
        foreach (var (id, loc) in b.Where(kv => a.ContainsKey(kv.Key)))
        {
            var old = a[id];
            if (old.ListId != loc.ListId || old.Index != loc.Index) lines.Add($"Moved step “{Label(loc.Step)}”.");
            var x = Definitions.Serialize(new WorkflowDefinition { Steps = [Strip(old.Step)] });
            var y = Definitions.Serialize(new WorkflowDefinition { Steps = [Strip(loc.Step)] });
            if (x != y) lines.Add($"Changed step “{Label(loc.Step)}”.");
        }

        var la = before.Lifecycle;
        var lb = after.Lifecycle;
        if (la is not null && lb is not null)
        {
            foreach (var s in lb.States.Where(s => la.States.All(o => o.Id != s.Id))) lines.Add($"Added state “{s.Label}”.");
            foreach (var s in la.States.Where(s => lb.States.All(o => o.Id != s.Id))) lines.Add($"Removed state “{s.Label}”.");
            foreach (var s in lb.States)
            {
                var o = la.States.FirstOrDefault(x => x.Id == s.Id);
                if (o is null) continue;
                if (o.Label != s.Label) lines.Add($"Renamed state “{o.Label}” to “{s.Label}”.");
                if (o.Key != s.Key) lines.Add($"State “{s.Label}” now stores “{s.Key}” instead of “{o.Key}”.");
                if (o.Category != s.Category) lines.Add($"State “{s.Label}” is now {s.Category}.");
            }
            foreach (var t in lb.Transitions.Where(t => la.Transitions.All(o => o.Id != t.Id))) lines.Add($"Added transition “{t.Label}”.");
            foreach (var t in la.Transitions.Where(t => lb.Transitions.All(o => o.Id != t.Id))) lines.Add($"Removed transition “{t.Label}”.");
            foreach (var t in lb.Transitions)
            {
                var o = la.Transitions.FirstOrDefault(x => x.Id == t.Id);
                if (o is null) continue;
                if (o.Label != t.Label) lines.Add($"Renamed transition “{o.Label}” to “{t.Label}”.");
                var x = Definitions.Serialize(new WorkflowDefinition { Lifecycle = new Lifecycle { Transitions = [StripTransition(o)] } });
                var y = Definitions.Serialize(new WorkflowDefinition { Lifecycle = new Lifecycle { Transitions = [StripTransition(t)] } });
                if (x != y) lines.Add($"Changed who can do or what happens on “{t.Label}”.");
            }
        }
        // Something differs that no sentence above names (positions, descriptions…); identical versions say nothing.
        if (lines.Count == 0 && Definitions.Serialize(before) != Definitions.Serialize(after)) lines.Add("Only layout or wording changes.");
        return lines;
    }

    private static WorkflowDefinition Wrap(Trigger? t) => new() { Trigger = t };

    private static string Label(Step s) => string.IsNullOrWhiteSpace(s.Title) ? Linter.StepLabels.GetValueOrDefault(s.Type, s.Type) : s.Title!;

    /// <summary>A copy of a step without its nested lists, so a child change is reported once, on the child.</summary>
    private static Step Strip(Step s)
    {
        var json = Definitions.Serialize(new WorkflowDefinition { Steps = [s] });
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!["steps"]![0]!.AsObject();
        foreach (var key in new[] { "paths", "otherwise", "cases", "steps", "outcomes", "onTimeout" })
        {
            if (node[key] is null) continue;
            node[key] = StripLists(node[key]!.DeepClone());
        }
        return System.Text.Json.JsonSerializer.Deserialize<Step>(node.ToJsonString(), Definitions.JsonOptions)!;
    }

    private static System.Text.Json.Nodes.JsonNode StripLists(System.Text.Json.Nodes.JsonNode n)
    {
        switch (n)
        {
            case System.Text.Json.Nodes.JsonObject o:
                if (o["steps"] is System.Text.Json.Nodes.JsonArray) o["steps"] = new System.Text.Json.Nodes.JsonArray();
                foreach (var key in new[] { "approved", "rejected", "timedOut" })
                    if (o[key] is System.Text.Json.Nodes.JsonArray) o[key] = new System.Text.Json.Nodes.JsonArray();
                return o;
            case System.Text.Json.Nodes.JsonArray a:
                if (a.All(x => x is System.Text.Json.Nodes.JsonObject { } xo && xo["type"] is not null)) return new System.Text.Json.Nodes.JsonArray();
                for (var i = 0; i < a.Count; i++) a[i] = StripLists(a[i]!.DeepClone());
                return a;
        }
        return n;
    }

    private static Transition StripTransition(Transition t) => new()
    {
        Id = t.Id, Key = t.Key, Label = "", From = t.From, To = t.To, Who = t.Who, When = t.When, Input = t.Input, Auto = t.Auto,
        Steps = t.Steps?.Select(s => (Step)new UnknownStep { Id = s.Id, Type = s.Type }).ToList(),
    };
}
