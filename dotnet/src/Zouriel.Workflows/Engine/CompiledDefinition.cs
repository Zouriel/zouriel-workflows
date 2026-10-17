using Zouriel.Workflows.Model;

namespace Zouriel.Workflows.Engine;

/// <summary>Where a step sits: the list that holds it and its index there.</summary>
internal sealed record StepLocation(Step Step, string ListId, int Index, string? ParentStepId);

/// <summary>
/// A parsed definition with every step list indexed. List ids: <c>root</c> for an automation's
/// steps; <c>transition:ID</c>, <c>onEnter:ID</c>, <c>onExit:ID</c>, <c>sla:ID</c>, <c>reset</c> for lifecycle runs;
/// <c>STEPID/SLOT</c> for lists nested in a step (e.g. <c>s3/path:p1</c>, <c>s5/outcome:approved</c>).
/// </summary>
internal sealed class CompiledDefinition
{
    public WorkflowDefinition Definition { get; }
    public string DefinitionId { get; }
    public int Version { get; }
    public IReadOnlyDictionary<string, List<Step>> Lists => _lists;
    public IReadOnlyDictionary<string, StepLocation> Steps => _steps;

    private readonly Dictionary<string, List<Step>> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepLocation> _steps = new(StringComparer.Ordinal);

    public CompiledDefinition(WorkflowDefinition definition, string definitionId, int version)
    {
        Definition = definition;
        DefinitionId = definitionId;
        Version = version;
        if (definition.Steps is not null) AddList("root", definition.Steps, null);
        if (definition.Lifecycle is { } lc)
        {
            foreach (var s in lc.States)
            {
                if (s.OnEnter is not null) AddList($"onEnter:{s.Id}", s.OnEnter, null);
                if (s.OnExit is not null) AddList($"onExit:{s.Id}", s.OnExit, null);
                if (s.Sla is not null) AddList($"sla:{s.Id}", s.Sla.Steps, null);
            }
            foreach (var t in lc.Transitions)
                if (t.Steps is not null) AddList($"transition:{t.Id}", t.Steps, null);
            if (lc.Reset?.Steps is { } resetSteps) AddList("reset", resetSteps, null);
        }
    }

    private void AddList(string listId, List<Step> steps, string? parent)
    {
        _lists[listId] = steps;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            // Duplicate ids are a lint error; the first one wins here so lookups stay deterministic.
            _steps.TryAdd(s.Id, new StepLocation(s, listId, i, parent));
            foreach (var (slot, child) in s.Children()) AddList($"{s.Id}/{slot}", child, s.Id);
            if (s.OnError is { Mode: "steps", Steps: { } errorSteps }) AddList($"{s.Id}/error", errorSteps, s.Id);
        }
    }

    public List<Step> List(string listId) =>
        _lists.TryGetValue(listId, out var l) ? l : throw new WorkflowException("brokenDefinition", $"Step list '{listId}' does not exist in this version.");

    public Step? Step(string id) => _steps.TryGetValue(id, out var loc) ? loc.Step : null;

    public Lifecycle Lifecycle => Definition.Lifecycle ?? throw new WorkflowException("notLifecycle", $"'{Definition.Key}' is not a lifecycle.");

    public State? StateById(string? id) => Definition.Lifecycle?.States.FirstOrDefault(s => s.Id == id);

    public State? StateByKey(object? value)
    {
        if (Definition.Lifecycle is null || value is null) return null;
        var text = Json.Values.Text(value);
        return Definition.Lifecycle.States.FirstOrDefault(s => s.Key == text);
    }

    public State InitialState => Lifecycle.States.FirstOrDefault(s => s.IsInitial) ?? Lifecycle.States[0];

    public Transition? TransitionByKey(string key) => Definition.Lifecycle?.Transitions.FirstOrDefault(t => t.Key == key);

    public Transition? TransitionById(string id) => Definition.Lifecycle?.Transitions.FirstOrDefault(t => t.Id == id);

    /// <summary>Variable key for a variable id.</summary>
    public string VariableKey(string id) => Definition.Variables?.FirstOrDefault(v => v.Id == id)?.Key ?? id;
}
