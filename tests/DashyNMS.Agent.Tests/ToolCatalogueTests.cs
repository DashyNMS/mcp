using System.ComponentModel;
using System.Reflection;
using DashyNMS.Agent.Tools;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The rules every tool must keep, checked over the whole assembly so a new
/// tool can't slip in without them.
/// </summary>
public sealed class ToolCatalogueTests
{
    private static IEnumerable<(MethodInfo Method, McpServerToolAttribute Tool)> Tools()
        => ToolsAssembly.Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (m, m.GetCustomAttribute<McpServerToolAttribute>()!))
            .Where(p => p.Item2 is not null);

    [Fact]
    public void The_tools_are_there()
    {
        string[] expected = ["active_alerts", "alert_details", "alert_rules", "config_backup", "config_diff", "device_details", "device_events", "device_health", "device_neighbours", "device_sensors", "find_device", "graph", "health_overview", "list_groups_and_locations", "network_events", "network_overview", "port_status", "recent_config_changes", "top_devices", "top_errors", "top_interfaces", "trace_endpoint", "what_changed"];

        Assert.Equal(expected, Tools().Select(t => t.Tool.Name).Order().ToArray());
    }

    [Fact]
    public void Every_tool_is_read_only()
    {
        Assert.All(Tools(), t =>
        {
            Assert.True(t.Tool.ReadOnly, t.Tool.Name);
            Assert.False(t.Tool.Destructive, t.Tool.Name);
        });
    }

    [Fact]
    public void Every_tool_and_parameter_is_described()
    {
        Assert.All(Tools(), t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Method.GetCustomAttribute<DescriptionAttribute>()?.Description), t.Tool.Name);
            foreach (var parameter in t.Method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
            {
                Assert.False(string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{t.Tool.Name}.{parameter.Name}");
            }
        });
    }
}
