using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DashyNMS.Agent.Catalogue;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// Holds the tools MCP lists equal to <see cref="ToolCatalogue"/>: names, titles, descriptions and
/// every parameter's schema, so a host reading the catalogue describes each tool exactly as MCP does.
/// </summary>
internal static partial class CatalogueMatch
{
    private static readonly HashSet<string> PropertyKeys = ["description", "type", "default", "items"];

    public static void AssertEqual(JsonElement tools)
    {
        var listed = tools.EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Equal(ToolCatalogue.Names.Order(StringComparer.Ordinal), listed.Keys.Order(StringComparer.Ordinal));

        foreach (var tool in ToolCatalogue.Tools)
        {
            var json = listed[tool.Name];
            Assert.Equal(tool.Title, json.GetProperty("title").GetString());
            Assert.Equal(tool.Title, json.GetProperty("annotations").GetProperty("title").GetString());
            Assert.Equal(tool.Description, json.GetProperty("description").GetString());

            var schema = json.GetProperty("inputSchema");
            var properties = schema.GetProperty("properties").EnumerateObject().ToList();
            Assert.Equal(tool.Parameters.Select(p => p.Name), properties.Select(p => p.Name));

            string[] required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
            Assert.Equal(tool.Parameters.Where(p => p.Required).Select(p => p.Name), required);

            foreach (var (parameter, property) in tool.Parameters.Zip(properties.Select(p => p.Value)))
            {
                var where = $"{tool.Name}.{parameter.Name}";
                Assert.True(property.EnumerateObject().All(k => PropertyKeys.Contains(k.Name)), $"{where} has a schema key the catalogue doesn't hold");
                Assert.Equal(parameter.Description, property.GetProperty("description").GetString());
                Assert.Equal(TypeOf(parameter), property.GetProperty("type").GetRawText());
                Assert.Equal(parameter.Type == ToolParameterType.StringList, property.TryGetProperty("items", out _));

                if (parameter.Required)
                {
                    Assert.False(property.TryGetProperty("default", out _), $"{where} is required but has a default");
                }
                else
                {
                    Assert.True(property.TryGetProperty("default", out var listedDefault), $"{where} is optional but has no default");
                    Assert.Equal(DefaultOf(parameter), listedDefault.GetRawText());
                }
            }
        }
    }

    /// <summary>The catalogue's documented values and ranges agree with the description a model reads.</summary>
    public static void AssertDescriptionsAgree()
    {
        foreach (var tool in ToolCatalogue.Tools)
        {
            foreach (var parameter in tool.Parameters)
            {
                var where = $"{tool.Name}.{parameter.Name}";
                foreach (var value in parameter.AllowedValues ?? Array.Empty<string>())
                {
                    Assert.Matches(new Regex($@"(?<![\w-]){Regex.Escape(value)}(?![\w-])"), parameter.Description);
                }

                if (Range().Match(parameter.Description) is { Success: true } range)
                {
                    Assert.Equal(int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture), parameter.Minimum);
                    Assert.Equal(int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture), parameter.Maximum);
                }

                if (Defaults().Match(parameter.Description) is { Success: true } stated && parameter.Type == ToolParameterType.Integer)
                {
                    Assert.Equal(int.Parse(stated.Groups[1].Value, CultureInfo.InvariantCulture), parameter.Default);
                }

                if (parameter.Minimum is { } min && parameter.Maximum is { } max)
                {
                    Assert.True(min <= max, where);
                }
            }
        }
    }

    private static string TypeOf(ToolParameter parameter)
    {
        var type = parameter.Type switch
        {
            ToolParameterType.String => "string",
            ToolParameterType.Integer => "integer",
            ToolParameterType.Boolean => "boolean",
            _ => "array",
        };
        var nullable = !parameter.Required && parameter.Default is null;
        return nullable ? $"[\"{type}\",\"null\"]" : $"\"{type}\"";
    }

    private static string DefaultOf(ToolParameter parameter) => parameter.Default switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => JsonSerializer.Serialize(s),
        var other => throw new InvalidOperationException($"Unexpected default {other}"),
    };

    [GeneratedRegex(@"(\d+) to (\d+)")]
    private static partial Regex Range();

    [GeneratedRegex(@"Defaults to (\d+)")]
    private static partial Regex Defaults();
}
