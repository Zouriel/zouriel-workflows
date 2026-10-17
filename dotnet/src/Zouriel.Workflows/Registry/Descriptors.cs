using System.Text.Json.Serialization;

namespace Zouriel.Workflows.Registry;

/// <summary>
/// Everything the host app lets workflows read, watch, write and call (spec §2). Served to the
/// builder at <c>GET /schema</c>; definitions may only reference what is listed here.
/// </summary>
public sealed class WorkflowRegistry
{
    /// <summary>The spec version of the registry format (currently <c>1</c>).</summary>
    public int SpecVersion { get; init; } = 1;
    /// <summary>The record types workflows may be about, with their fields.</summary>
    public List<EntityDescriptor> Entities { get; init; } = [];
    /// <summary>App actions <c>action</c> steps may call.</summary>
    public List<ActionDescriptor> Actions { get; init; } = [];
    /// <summary>Named events the app raises (<c>signal</c> triggers and waits).</summary>
    public List<SignalDescriptor> Signals { get; init; } = [];
    /// <summary>Notification channels (e.g. <c>email</c>, <c>inApp</c>).</summary>
    public List<LabelledItem> Channels { get; init; } = [];
    /// <summary>Roles assignee lists may name.</summary>
    public List<LabelledItem> Roles { get; init; } = [];
    /// <summary>Groups assignee lists may name.</summary>
    public List<LabelledItem> Groups { get; init; } = [];

    /// <summary>The entity with this key, or null.</summary>
    /// <param name="key">The entity key (null finds nothing).</param>
    public EntityDescriptor? Entity(string? key) => key is null ? null : Entities.FirstOrDefault(e => e.Key == key);

    /// <summary>A field of an entity, or null when either is unknown.</summary>
    /// <param name="entity">The entity key.</param>
    /// <param name="field">The field key.</param>
    public FieldDescriptor? Field(string? entity, string? field) => Entity(entity)?.Field(field);

    /// <summary>The action with this key, or null.</summary>
    /// <param name="key">The action key.</param>
    public ActionDescriptor? Action(string? key) => Actions.FirstOrDefault(a => a.Key == key);

    /// <summary>The signal with this key, or null.</summary>
    /// <param name="key">The signal key.</param>
    public SignalDescriptor? Signal(string? key) => Signals.FirstOrDefault(s => s.Key == key);

    /// <summary>Reads a registry from its JSON form (spec <c>registry.schema.json</c>), e.g. for gateways that are not EF-bound.</summary>
    /// <param name="json">The registry JSON.</param>
    public static WorkflowRegistry FromJson(string json)
    {
        var r = System.Text.Json.JsonSerializer.Deserialize<WorkflowRegistry>(json, Model.Definitions.JsonOptions) ?? new WorkflowRegistry();
        foreach (var f in r.Entities.SelectMany(e => e.Fields))
            foreach (var o in f.Options ?? []) NormalizeOption(o);
        return r;
    }

    private static void NormalizeOption(FieldOption o) => o.Value = Json.Values.Normalize(o.Value) ?? "";

    /// <summary>Writes the registry as JSON (CLR bindings are left out).</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Model.Definitions.JsonOptions);
}

/// <summary>A key with a label, for channels, roles and groups.</summary>
public sealed class LabelledItem
{
    /// <summary>The key definitions use.</summary>
    public string Key { get; init; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; init; } = "";
    /// <summary>Optional explanation.</summary>
    public string? Description { get; init; }
}

/// <summary>The field <c>type</c> values (spec §2).</summary>
public static class FieldTypes
{
    /// <summary>Single-line text.</summary>
    public const string Text = "text";
    /// <summary>Multi-line text.</summary>
    public const string LongText = "longText";
    /// <summary>An email address.</summary>
    public const string Email = "email";
    /// <summary>A phone number.</summary>
    public const string Phone = "phone";
    /// <summary>A web address.</summary>
    public const string Url = "url";
    /// <summary>A number.</summary>
    public const string Number = "number";
    /// <summary>An amount of money (a number).</summary>
    public const string Currency = "currency";
    /// <summary>True or false.</summary>
    public const string Boolean = "boolean";
    /// <summary>A calendar date, <c>YYYY-MM-DD</c>.</summary>
    public const string Date = "date";
    /// <summary>An instant, ISO 8601 UTC.</summary>
    public const string DateTime = "dateTime";
    /// <summary>One of the field's options (stored value).</summary>
    public const string Enum = "enum";
    /// <summary>A user id.</summary>
    public const string User = "user";
    /// <summary>One related record of the field's entity (its id).</summary>
    public const string Relation = "relation";
    /// <summary>Many related records of the field's entity.</summary>
    public const string Collection = "collection";
    /// <summary>Free JSON.</summary>
    public const string Json = "json";

    /// <summary>True for types holding a single value (not relations or collections).</summary>
    /// <param name="type">A field type.</param>
    public static bool IsScalar(string type) => type is not (Relation or Collection);
}

/// <summary>A record type the app registered, with the fields workflows may use.</summary>
public sealed class EntityDescriptor
{
    /// <summary>The entity key definitions use.</summary>
    public string Key { get; init; } = "";
    /// <summary>The singular name shown to people.</summary>
    public string Label { get; init; } = "";
    /// <summary>The plural name; null uses the label.</summary>
    public string? PluralLabel { get; init; }
    /// <summary>The field whose value names a record (in tasks and messages).</summary>
    public string? TitleField { get; init; }
    /// <summary>The registered fields.</summary>
    public List<FieldDescriptor> Fields { get; init; } = [];

    /// <summary>The CLR type registered for this entity, when bound with the fluent builder.</summary>
    [JsonIgnore] public Type? ClrType { get; init; }
    /// <summary>Reads the record id from a bound CLR entity (fluent builder only).</summary>
    [JsonIgnore] public Func<object, object?>? IdGetter { get; init; }

    /// <summary>The field with this key, or null.</summary>
    /// <param name="key">The field key.</param>
    public FieldDescriptor? Field(string? key) => key is null ? null : Fields.FirstOrDefault(f => f.Key == key);
}

/// <summary>One option of an enum field.</summary>
public sealed class FieldOption
{
    /// <summary>The stored value.</summary>
    public object Value { get; set; } = "";
    /// <summary>The text shown to people (and rendered by templates).</summary>
    public string Label { get; init; } = "";
}

/// <summary>A field of an entity and what workflows may do with it (spec §2 flags).</summary>
public sealed class FieldDescriptor
{
    /// <summary>The field key token paths use.</summary>
    public string Key { get; init; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; init; } = "";
    /// <summary>One of <see cref="FieldTypes"/>.</summary>
    public string Type { get; init; } = FieldTypes.Text;
    /// <summary>The options of an enum field.</summary>
    public List<FieldOption>? Options { get; init; }
    /// <summary>For relations and collections: the related entity's key.</summary>
    public string? Entity { get; init; }
    /// <summary>False hides the field from tokens; null or true = readable.</summary>
    public bool? Readable { get; init; }
    /// <summary>True lets triggers and condition waits watch it.</summary>
    public bool? Watchable { get; init; }
    /// <summary>True lets <c>setFields</c> write it.</summary>
    public bool? Settable { get; init; }
    /// <summary>True lets a lifecycle own it (enum fields only).</summary>
    public bool? Lifecycle { get; init; }
    /// <summary>True masks its values in audit, views and test runs.</summary>
    public bool? Sensitive { get; init; }
    /// <summary>True when the app requires a value.</summary>
    public bool? Required { get; init; }

    /// <summary>True unless <see cref="Readable"/> is false.</summary>
    [JsonIgnore] public bool IsReadable => Readable != false;
    /// <summary>True when <see cref="Watchable"/> is true.</summary>
    [JsonIgnore] public bool IsWatchable => Watchable == true;
    /// <summary>True when <see cref="Settable"/> is true.</summary>
    [JsonIgnore] public bool IsSettable => Settable == true;
    /// <summary>True when a lifecycle may own the field (flagged and an enum).</summary>
    [JsonIgnore] public bool IsLifecycle => Lifecycle == true && Type == FieldTypes.Enum;
    /// <summary>True when <see cref="Sensitive"/> is true.</summary>
    [JsonIgnore] public bool IsSensitive => Sensitive == true;

    /// <summary>Reads the field from a bound CLR entity (fluent builder only).</summary>
    [JsonIgnore] public Func<object, object?>? Getter { get; init; }
    /// <summary>Writes a workflow value to a bound CLR entity (fluent builder only).</summary>
    [JsonIgnore] public Action<object, object?>? Setter { get; init; }
    /// <summary>The CLR property path, e.g. <c>Status</c> or <c>Applicant.Email</c>.</summary>
    [JsonIgnore] public string? MemberPath { get; init; }

    /// <summary>The label of an enum value, or the value as text when it is not an option.</summary>
    /// <param name="value">The stored value.</param>
    public string OptionLabel(object? value)
    {
        if (Options is null || value is null) return Json.Values.Text(value);
        var text = Json.Values.Text(value);
        return Options.FirstOrDefault(o => Json.Values.Text(o.Value) == text)?.Label ?? text;
    }
}

/// <summary>An input or output of an action, or a data field of a signal.</summary>
public sealed class ParamDescriptor
{
    /// <summary>The key steps and tokens use.</summary>
    public string Key { get; init; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; init; } = "";
    /// <summary>One of <see cref="FieldTypes"/>.</summary>
    public string Type { get; init; } = FieldTypes.Text;
    /// <summary>True when an action input must have a value.</summary>
    public bool? Required { get; init; }
    /// <summary>The options of an enum parameter.</summary>
    public List<FieldOption>? Options { get; init; }
    /// <summary>For relation parameters: the related entity's key.</summary>
    public string? Entity { get; init; }
    /// <summary>Optional explanation.</summary>
    public string? Description { get; init; }
}

/// <summary>An app action <c>action</c> steps may call, with typed inputs and outputs.</summary>
public sealed class ActionDescriptor
{
    /// <summary>The action key (e.g. <c>finance.createInvoice</c>).</summary>
    public string Key { get; init; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; init; } = "";
    /// <summary>Optional explanation.</summary>
    public string? Description { get; init; }
    /// <summary>Groups actions in the step picker.</summary>
    public string? Category { get; init; }
    /// <summary>The record type it works on; null = any.</summary>
    public string? Entity { get; init; }
    /// <summary>Its inputs.</summary>
    public List<ParamDescriptor> Inputs { get; init; } = [];
    /// <summary>Its outputs (<c>steps.&lt;id&gt;.&lt;key&gt;</c>).</summary>
    public List<ParamDescriptor> Outputs { get; init; } = [];

    /// <summary>The handler type resolved from DI when the step runs.</summary>
    [JsonIgnore] public Type? HandlerType { get; init; }
}

/// <summary>A named event the app raises, optionally about one record type.</summary>
public sealed class SignalDescriptor
{
    /// <summary>The signal key (e.g. <c>payment.received</c>).</summary>
    public string Key { get; init; } = "";
    /// <summary>The name shown to people.</summary>
    public string Label { get; init; } = "";
    /// <summary>The record type it is about; null = no record.</summary>
    public string? Entity { get; init; }
    /// <summary>Its data fields (<c>trigger.data.*</c>, wait output <c>data</c>).</summary>
    public List<ParamDescriptor> Fields { get; init; } = [];
}
