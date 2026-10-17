using System.Text.Json.Serialization;

namespace Zouriel.Workflows.Registry;

/// <summary>
/// Everything the host app lets workflows read, watch, write and call (spec §2). Served to the
/// builder at <c>GET /schema</c>; definitions may only reference what is listed here.
/// </summary>
public sealed class WorkflowRegistry
{
    public int SpecVersion { get; init; } = 1;
    public List<EntityDescriptor> Entities { get; init; } = [];
    public List<ActionDescriptor> Actions { get; init; } = [];
    public List<SignalDescriptor> Signals { get; init; } = [];
    public List<LabelledItem> Channels { get; init; } = [];
    public List<LabelledItem> Roles { get; init; } = [];
    public List<LabelledItem> Groups { get; init; } = [];

    public EntityDescriptor? Entity(string? key) => key is null ? null : Entities.FirstOrDefault(e => e.Key == key);

    public FieldDescriptor? Field(string? entity, string? field) => Entity(entity)?.Field(field);

    public ActionDescriptor? Action(string? key) => Actions.FirstOrDefault(a => a.Key == key);

    public SignalDescriptor? Signal(string? key) => Signals.FirstOrDefault(s => s.Key == key);

    /// <summary>Reads a registry from its JSON form (spec <c>registry.schema.json</c>), e.g. for gateways that are not EF-bound.</summary>
    public static WorkflowRegistry FromJson(string json)
    {
        var r = System.Text.Json.JsonSerializer.Deserialize<WorkflowRegistry>(json, Model.Definitions.JsonOptions) ?? new WorkflowRegistry();
        foreach (var f in r.Entities.SelectMany(e => e.Fields))
            foreach (var o in f.Options ?? []) NormalizeOption(o);
        return r;
    }

    private static void NormalizeOption(FieldOption o) => o.Value = Json.Values.Normalize(o.Value) ?? "";

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Model.Definitions.JsonOptions);
}

public sealed class LabelledItem
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
}

public static class FieldTypes
{
    public const string Text = "text";
    public const string LongText = "longText";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Url = "url";
    public const string Number = "number";
    public const string Currency = "currency";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "dateTime";
    public const string Enum = "enum";
    public const string User = "user";
    public const string Relation = "relation";
    public const string Collection = "collection";
    public const string Json = "json";

    public static bool IsScalar(string type) => type is not (Relation or Collection);
}

public sealed class EntityDescriptor
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? PluralLabel { get; init; }
    public string? TitleField { get; init; }
    public List<FieldDescriptor> Fields { get; init; } = [];

    /// <summary>The CLR type registered for this entity, when bound with the fluent builder.</summary>
    [JsonIgnore] public Type? ClrType { get; init; }
    [JsonIgnore] public Func<object, object?>? IdGetter { get; init; }

    public FieldDescriptor? Field(string? key) => key is null ? null : Fields.FirstOrDefault(f => f.Key == key);
}

public sealed class FieldOption
{
    public object Value { get; set; } = "";
    public string Label { get; init; } = "";
}

public sealed class FieldDescriptor
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Type { get; init; } = FieldTypes.Text;
    public List<FieldOption>? Options { get; init; }
    public string? Entity { get; init; }
    public bool? Readable { get; init; }
    public bool? Watchable { get; init; }
    public bool? Settable { get; init; }
    public bool? Lifecycle { get; init; }
    public bool? Sensitive { get; init; }
    public bool? Required { get; init; }

    [JsonIgnore] public bool IsReadable => Readable != false;
    [JsonIgnore] public bool IsWatchable => Watchable == true;
    [JsonIgnore] public bool IsSettable => Settable == true;
    [JsonIgnore] public bool IsLifecycle => Lifecycle == true && Type == FieldTypes.Enum;
    [JsonIgnore] public bool IsSensitive => Sensitive == true;

    /// <summary>Reads the field from a bound CLR entity (fluent builder only).</summary>
    [JsonIgnore] public Func<object, object?>? Getter { get; init; }
    /// <summary>Writes a workflow value to a bound CLR entity (fluent builder only).</summary>
    [JsonIgnore] public Action<object, object?>? Setter { get; init; }
    /// <summary>The CLR property path, e.g. <c>Status</c> or <c>Applicant.Email</c>.</summary>
    [JsonIgnore] public string? MemberPath { get; init; }

    public string OptionLabel(object? value)
    {
        if (Options is null || value is null) return Json.Values.Text(value);
        var text = Json.Values.Text(value);
        return Options.FirstOrDefault(o => Json.Values.Text(o.Value) == text)?.Label ?? text;
    }
}

public sealed class ParamDescriptor
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Type { get; init; } = FieldTypes.Text;
    public bool? Required { get; init; }
    public List<FieldOption>? Options { get; init; }
    public string? Entity { get; init; }
    public string? Description { get; init; }
}

public sealed class ActionDescriptor
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Entity { get; init; }
    public List<ParamDescriptor> Inputs { get; init; } = [];
    public List<ParamDescriptor> Outputs { get; init; } = [];

    /// <summary>The handler type resolved from DI when the step runs.</summary>
    [JsonIgnore] public Type? HandlerType { get; init; }
}

public sealed class SignalDescriptor
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Entity { get; init; }
    public List<ParamDescriptor> Fields { get; init; } = [];
}
