using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zouriel.Workflows.Json;

namespace Zouriel.Workflows.Model;

/// <summary>Reading and writing definitions.</summary>
public static class Definitions
{
    /// <summary>The JSON settings every workflow document uses: camelCase names, nulls left out, comments and trailing commas allowed.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a definition from JSON text.</summary>
    /// <param name="json">The definition JSON.</param>
    /// <exception cref="JsonException">The text is not a definition.</exception>
    public static WorkflowDefinition Parse(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonOptions) ?? throw new JsonException("Empty definition.");

    /// <summary>Reads a definition from a parsed JSON node.</summary>
    /// <param name="json">The definition JSON.</param>
    /// <exception cref="JsonException">The node is not a definition.</exception>
    public static WorkflowDefinition Parse(JsonNode json) =>
        json.Deserialize<WorkflowDefinition>(JsonOptions) ?? throw new JsonException("Empty definition.");

    /// <summary>Writes a definition as JSON.</summary>
    /// <param name="d">The definition.</param>
    public static string Serialize(WorkflowDefinition d) => JsonSerializer.Serialize(d, JsonOptions);

    internal static object? ReadValue(ref Utf8JsonReader reader) => Values.FromJson(JsonNode.Parse(ref reader));
}

internal sealed class StepConverter : JsonConverter<Step>
{
    public override Step? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A step must be an object.");
        var type = node["type"]?.GetValue<string>() ?? "";
        if (!StepTypes.All.TryGetValue(type, out var clr))
        {
            return new UnknownStep { Id = node["id"]?.GetValue<string>() ?? "", Type = type, Title = node["title"]?.GetValue<string>() };
        }
        return (Step)node.Deserialize(clr, options)!;
    }

    public override void Write(Utf8JsonWriter writer, Step value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

internal sealed class OnErrorConverter : JsonConverter<OnError>
{
    public override OnError? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new OnError { Mode = reader.GetString() ?? "stop" };
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("onError must be a string or an object.");
        return new OnError { Mode = "steps", Steps = node["steps"].Deserialize<List<Step>>(options) ?? [] };
    }

    public override void Write(Utf8JsonWriter writer, OnError value, JsonSerializerOptions options)
    {
        if (value.Mode != "steps")
        {
            writer.WriteStringValue(value.Mode);
            return;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("steps");
        JsonSerializer.Serialize(writer, value.Steps ?? [], options);
        writer.WriteEndObject();
    }
}

internal sealed class OperandConverter : JsonConverter<Operand>
{
    public override Operand? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("An operand must be an object.");
        if (node.TryGetPropertyValue("const", out var c)) return new Operand { HasConst = true, Const = Values.FromJson(c) };
        if (node["ref"] is JsonValue r) return new Operand { Ref = r.GetValue<string>() };
        if (node["today"] is JsonValue t) return new Operand { Today = (int)t.GetValue<double>() };
        if (node["calc"] is JsonObject calc) return new Operand { Calc = calc.Deserialize<Calc>(options) };
        // A stored {} is an unfinished operand.
        return new Operand();
    }

    public override void Write(Utf8JsonWriter writer, Operand value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.HasConst)
        {
            writer.WritePropertyName("const");
            var j = Values.ToJson(value.Const);
            if (j is null) writer.WriteNullValue();
            else j.WriteTo(writer);
        }
        else if (value.Ref is not null) writer.WriteString("ref", value.Ref);
        else if (value.Today is not null) writer.WriteNumber("today", value.Today.Value);
        else if (value.Calc is not null)
        {
            writer.WritePropertyName("calc");
            JsonSerializer.Serialize(writer, value.Calc, options);
        }
        writer.WriteEndObject();
    }
}

internal sealed class CalcArgConverter : JsonConverter<CalcArg>
{
    public override CalcArg? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A calculation argument must be an object.");
        if (node["ref"] is JsonValue r) return new CalcArg { Ref = r.GetValue<string>() };
        if (node.TryGetPropertyValue("const", out var c)) return new CalcArg { HasConst = true, Const = Values.FromJson(c) };
        if (node["calc"] is JsonObject calc) return new CalcArg { Calc = calc.Deserialize<Calc>(options) };
        return new CalcArg();
    }

    public override void Write(Utf8JsonWriter writer, CalcArg value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Ref is not null) writer.WriteString("ref", value.Ref);
        else if (value.HasConst)
        {
            writer.WritePropertyName("const");
            var j = Values.ToJson(value.Const);
            if (j is null) writer.WriteNullValue();
            else j.WriteTo(writer);
        }
        else if (value.Calc is not null)
        {
            writer.WritePropertyName("calc");
            JsonSerializer.Serialize(writer, value.Calc, options);
        }
        writer.WriteEndObject();
    }
}

internal sealed class TemplateConverter : JsonConverter<Template>
{
    public override Template? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return Template.Of(reader.GetString() ?? "");
        var node = JsonNode.Parse(ref reader) as JsonArray ?? throw new JsonException("A template must be a string or an array.");
        var parts = new List<object>();
        foreach (var p in node)
        {
            if (p is JsonValue v && v.TryGetValue<string>(out var s)) parts.Add(s);
            else if (p is JsonObject o && o["ref"] is JsonValue r) parts.Add(new TemplateRef(r.GetValue<string>()));
        }
        return new Template { Parts = parts };
    }

    public override void Write(Utf8JsonWriter writer, Template value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var p in value.Parts)
        {
            if (p is TemplateRef r)
            {
                writer.WriteStartObject();
                writer.WriteString("ref", r.Ref);
                writer.WriteEndObject();
            }
            else writer.WriteStringValue(p.ToString());
        }
        writer.WriteEndArray();
    }
}

internal sealed class ConditionConverter : JsonConverter<Condition>
{
    public override Condition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A condition must be an object.");
        return ReadNode(node, options);
    }

    private static Condition ReadNode(JsonObject node, JsonSerializerOptions options)
    {
        if (node["conditions"] is JsonArray arr)
        {
            return new ConditionGroup
            {
                Match = node["match"]?.GetValue<string>() ?? "all",
                Conditions = arr.OfType<JsonObject>().Select(n => ReadNode(n, options)).ToList(),
            };
        }
        return new ConditionRule
        {
            Ref = node["ref"]?.GetValue<string>() ?? "",
            Op = node["op"]?.GetValue<string>() ?? "eq",
            Value = node["value"]?.Deserialize<Operand>(options),
            Value2 = node["value2"]?.Deserialize<Operand>(options),
        };
    }

    public override void Write(Utf8JsonWriter writer, Condition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case ConditionGroup g:
                writer.WriteString("match", g.Match);
                writer.WritePropertyName("conditions");
                writer.WriteStartArray();
                foreach (var c in g.Conditions) Write(writer, c, options);
                writer.WriteEndArray();
                break;
            case ConditionRule r:
                writer.WriteString("ref", r.Ref);
                writer.WriteString("op", r.Op);
                if (r.Value is not null)
                {
                    writer.WritePropertyName("value");
                    JsonSerializer.Serialize(writer, r.Value, options);
                }
                if (r.Value2 is not null)
                {
                    writer.WritePropertyName("value2");
                    JsonSerializer.Serialize(writer, r.Value2, options);
                }
                break;
        }
        writer.WriteEndObject();
    }
}

internal sealed class TransitionFromConverter : JsonConverter<TransitionFrom>
{
    public override TransitionFrom? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() == "*" ? new TransitionFrom { Any = true } : new TransitionFrom { States = [reader.GetString()!] };
        }
        return new TransitionFrom { States = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [] };
    }

    public override void Write(Utf8JsonWriter writer, TransitionFrom value, JsonSerializerOptions options)
    {
        if (value.Any) writer.WriteStringValue("*");
        else JsonSerializer.Serialize(writer, value.States, options);
    }
}
