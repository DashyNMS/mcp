using System.Text.Json;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// Holds JSON to the JSON Schema keywords the tools' output schemas use, the
/// way a strict client would - and stricter: a property the schema doesn't
/// declare fails too, so a schema can't fall behind what a tool returns.
/// A keyword it doesn't know fails rather than passing unchecked.
/// </summary>
internal static class JsonSchemaCheck
{
    private static readonly HashSet<string> Known = ["type", "properties", "required", "items", "anyOf", "enum", "additionalProperties", "description"];

    /// <summary>What's wrong, one line per problem; empty when <paramref name="value"/> fits.</summary>
    public static IReadOnlyList<string> Problems(JsonElement schema, JsonElement value)
    {
        var problems = new List<string>();
        Check(schema, value, "$", problems);
        return problems;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            problems.Add($"{path}: the schema allows anything");
            return;
        }

        foreach (var keyword in schema.EnumerateObject().Where(k => !Known.Contains(k.Name)))
        {
            problems.Add($"{path}: unknown keyword {keyword.Name}");
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var attempts = anyOf.EnumerateArray().Select(option => Problems(option, value)).ToList();
            if (attempts.All(a => a.Count > 0))
            {
                problems.Add($"{path}: fits none of anyOf ({string.Join(" | ", attempts.Select(a => string.Join("; ", a)))})");
            }

            return;
        }

        if (schema.TryGetProperty("type", out var type) && !Types(type).Any(t => Is(t, value)))
        {
            problems.Add($"{path}: is {value.ValueKind}, not {type.GetRawText()}");
            return;
        }

        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)))
        {
            problems.Add($"{path}: {value.GetRawText()} isn't one of {values.GetRawText()}");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            CheckObject(schema, value, path, problems);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Check(items, item, $"{path}[{index++}]", problems);
            }
        }
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        var declared = schema.TryGetProperty("properties", out var properties);
        var others = schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var property in value.EnumerateObject())
        {
            if (declared && properties.TryGetProperty(property.Name, out var own))
            {
                Check(own, property.Value, $"{path}.{property.Name}", problems);
            }
            else if (others && additional.ValueKind == JsonValueKind.Object)
            {
                Check(additional, property.Value, $"{path}.{property.Name}", problems);
            }
            else if (declared)
            {
                problems.Add($"{path}.{property.Name}: not declared");
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            problems.AddRange(required.EnumerateArray()
                .Where(name => !value.TryGetProperty(name.GetString()!, out _))
                .Select(name => $"{path}.{name.GetString()}: missing"));
        }
    }

    private static IEnumerable<string> Types(JsonElement type)
        => type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!) : [type.GetString()!];

    private static bool Is(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => false,
    };
}
