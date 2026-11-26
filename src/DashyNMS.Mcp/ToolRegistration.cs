using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent;
using DashyNMS.Agent.Outputs;
using DashyNMS.Agent.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DashyNMS.Mcp;

/// <summary>
/// Registers the tool layer's tools the way <c>WithToolsFromAssembly</c>
/// does, but reading arguments with <see cref="ToolArguments.Options"/>.
/// </summary>
/// <remarks>
/// That reader takes a number where text is expected (#13's trial: a model
/// sent a sensor ID as 16197 and the call failed before the tool ran). A
/// custom string converter costs the generated schemas their "type", though,
/// so each text parameter's type is put back from the method's own
/// signature: ["string", "null"] when it's optional, "string" when not.
/// Each tool also gets its output schema (#40), which <see cref="ToolCallFilters.WithStructuredContent"/>
/// answers with structured content.
/// </remarks>
public static class ToolRegistration
{
    /// <param name="policy">Decides which tools are registered at all - see <see cref="ToolPolicy"/>.</param>
    public static IMcpServerBuilder WithDashyNmsTools(this IMcpServerBuilder builder, ToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);
        foreach (var type in ToolsAssembly.Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name is { } name && policy.IsOn(name)))
            {
                builder.Services.AddSingleton(services =>
                {
                    var tool = McpServerTool.Create(
                        method,
                        request => ActivatorUtilities.CreateInstance(request.Services ?? services, type),
                        new McpServerToolCreateOptions { Services = services, SerializerOptions = ToolArguments.Options });
                    tool.ProtocolTool.InputSchema = WithTextTypes(tool.ProtocolTool.InputSchema, method);
                    tool.ProtocolTool.OutputSchema = ToolOutputSchema.For(tool.ProtocolTool.Name);
                    return tool;
                });
            }
        }

        return builder;
    }

    /// <summary>Puts "type" back on every text parameter's schema that lost it.</summary>
    internal static JsonElement WithTextTypes(JsonElement schema, MethodInfo method)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root || root["properties"] is not JsonObject properties)
        {
            return schema;
        }

        var nullability = new NullabilityInfoContext();
        foreach (var parameter in method.GetParameters().Where(p => p.ParameterType == typeof(string)))
        {
            if (parameter.Name is { } name && properties[name] is JsonObject property && !property.ContainsKey("type"))
            {
                var optional = parameter.HasDefaultValue || nullability.Create(parameter).WriteState == NullabilityState.Nullable;
                property["type"] = optional ? new JsonArray("string", "null") : "string";
            }
        }

        return JsonSerializer.SerializeToElement(root);
    }
}
