using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The catalogue's ranges, defaults and documented values against the limits the tools enforce, so a
/// changed limit can't leave a host offering a model a range the tool refuses. DashyNMS.Mcp.Tests holds
/// the same catalogue to the descriptions and schemas MCP lists.
/// </summary>
public sealed class CatalogueLimitsTests
{
    private static ToolParameter Parameter(string tool, string name)
        => ToolCatalogue.Find(tool)!.Parameters.Single(p => p.Name == name);

    public static TheoryData<string, int, int> Limits() => new()
    {
        { ToolText.FindDevice.Name, DeviceTools.DefaultMatches, DeviceTools.MaxMatches },
        { ToolText.ActiveAlerts.Name, AlertTools.DefaultAlerts, AlertTools.MaxAlerts },
        { ToolText.AlertRules.Name, RuleTools.DefaultRules, RuleTools.MaxRules },
        { ToolText.DeviceDetails.Name, DetailTools.DefaultRows, DetailTools.MaxRows },
        { ToolText.TopInterfaces.Name, NetworkTools.DefaultTop, NetworkTools.MaxTop },
        { ToolText.TopErrors.Name, NetworkTools.DefaultTop, NetworkTools.MaxTop },
        { ToolText.TopDevices.Name, NetworkTools.DefaultTop, NetworkTools.MaxTop },
        { ToolText.HealthOverview.Name, HealthTools.DefaultReadings, HealthTools.MaxReadings },
        { ToolText.DeviceEvents.Name, EventTools.DefaultEvents, EventTools.MaxEvents },
        { ToolText.NetworkEvents.Name, NetworkTools.DefaultEvents, NetworkTools.MaxEvents },
        { ToolText.ConfigBackup.Name, ConfigTools.DefaultLines, ConfigTools.MaxLines },
        { ToolText.RecentConfigChanges.Name, ConfigTools.DefaultChanges, ConfigTools.MaxChanges },
        { ToolText.SearchLogs.Name, LogTools.DefaultLines, LogTools.MaxLines },
    };

    [Theory]
    [MemberData(nameof(Limits))]
    public void Each_limit_matches_the_tool(string tool, int defaultLimit, int maximum)
    {
        var limit = Parameter(tool, "limit");
        Assert.Equal(defaultLimit, limit.Default);
        Assert.Equal(maximum, limit.Maximum);
    }

    [Fact]
    public void Every_hours_parameter_matches_the_window_limit()
        => Assert.All(ToolCatalogue.Tools.SelectMany(t => t.Parameters).Where(p => p.Name == "hours"), p =>
        {
            Assert.Equal(1, p.Minimum);
            Assert.Equal(TimeWindow.MaxHours, p.Maximum);
        });

    [Fact]
    public void Device_details_offers_exactly_its_sections()
        => Assert.Equal(DetailTools.AllSections, Parameter(ToolText.DeviceDetails.Name, "sections").AllowedValues);
}
