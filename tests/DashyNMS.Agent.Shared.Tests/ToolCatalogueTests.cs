using System.Text.RegularExpressions;
using DashyNMS.Agent.Catalogue;

namespace DashyNMS.Agent.Shared.Tests;

/// <summary>
/// The catalogue's own rules. That it equals what MCP lists is checked in DashyNMS.Mcp.Tests, which
/// can start the server; these hold for any host reading the package.
/// </summary>
public sealed class ToolCatalogueTests
{
    /// <summary>The tools whose family a description names as "the top_ tools".</summary>
    private static readonly string[] TopTools = [ToolText.TopInterfaces.Name, ToolText.TopErrors.Name, ToolText.TopDevices.Name];

    [Fact]
    public void It_holds_every_tool_once()
    {
        string[] expected = ["active_alerts", "alert_details", "alert_rules", "config_backup", "config_diff", "device_details", "device_events", "device_health", "device_neighbours", "device_sensors", "find_device", "graph", "health_overview", "list_groups_and_locations", "network_events", "network_overview", "port_status", "recent_config_changes", "search_logs", "top_devices", "top_errors", "top_interfaces", "trace_endpoint", "what_changed"];

        Assert.Equal(expected, ToolCatalogue.Names.Order(StringComparer.Ordinal));
        Assert.Equal(ToolCatalogue.Tools.Select(t => t.Name), ToolCatalogue.Names);
    }

    [Fact]
    public void Tools_are_found_by_their_exact_name()
    {
        Assert.Same(ToolCatalogue.Tools[0], ToolCatalogue.Find(ToolCatalogue.Tools[0].Name));
        Assert.Equal("Device health", ToolCatalogue.Find("device_health")?.Title);
        Assert.Null(ToolCatalogue.Find("Device_Health"));
        Assert.Null(ToolCatalogue.Find("device_summary"));
        Assert.Null(ToolCatalogue.Find(""));
    }

    [Fact]
    public void Every_tool_and_parameter_is_described_and_consistent()
    {
        Assert.All(ToolCatalogue.Tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Title), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.Equal(tool.Parameters.Count, tool.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(tool.Parameters, p =>
            {
                var where = $"{tool.Name}.{p.Name}";
                Assert.False(string.IsNullOrWhiteSpace(p.Description), where);
                Assert.Matches("^[a-z][a-z_]*$", p.Name);
                Assert.False(p.Required && p.Default is not null, $"{where} is required and has a default");
                Assert.True(p.Default switch
                {
                    null => true,
                    int => p.Type == ToolParameterType.Integer,
                    bool => p.Type == ToolParameterType.Boolean,
                    string => p.Type == ToolParameterType.String,
                    _ => false,
                }, $"{where}'s default doesn't match its type");
                Assert.True(p.AllowedValues is null || p.Type is ToolParameterType.String or ToolParameterType.StringList, where);
                Assert.True((p.Minimum is null && p.Maximum is null) || p.Type == ToolParameterType.Integer, where);
                if (p.Default is int value)
                {
                    Assert.InRange(value, p.Minimum ?? int.MinValue, p.Maximum ?? int.MaxValue);
                }
            });
        });
    }

    [Fact]
    public void Device_health_names_the_tools_it_sends_a_model_to()
        => Assert.Equal(new[] { "port_status", "device_events", "what_changed" }, ToolCatalogue.Find("device_health")!.NamedTools);

    /// <summary>
    /// The recorded names are exactly the tools the text mentions, so a reworded description can't leave
    /// a host offering a tool set whose descriptions point at a tool it doesn't have. <c>graph</c> is left
    /// out of the scan: the word appears in ordinary prose ("a device's graphs") far more than as the tool.
    /// </summary>
    [Fact]
    public void Named_tools_are_the_tools_each_description_mentions()
    {
        foreach (var tool in ToolCatalogue.Tools)
        {
            var text = tool.Description + " " + string.Join(" ", tool.Parameters.Select(p => p.Description));
            var mentioned = ToolCatalogue.Names
                .Where(n => n != tool.Name && n.Contains('_', StringComparison.Ordinal)
                    && Regex.IsMatch(text, $"(?<![A-Za-z0-9_]){Regex.Escape(n)}(?![A-Za-z0-9_])"))
                .Union(text.Contains("the top_ tools", StringComparison.Ordinal) ? TopTools.Where(n => n != tool.Name) : Enumerable.Empty<string>())
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(ToolCatalogue.Names.Where(mentioned.Contains), tool.NamedTools);
        }
    }

    [Fact]
    public void Named_tools_are_catalogue_tools_other_than_itself()
        => Assert.All(ToolCatalogue.Tools, tool => Assert.All(tool.NamedTools, n =>
        {
            Assert.NotNull(ToolCatalogue.Find(n));
            Assert.NotEqual(tool.Name, n);
        }));
}
