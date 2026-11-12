using System.Net;
using DashyNMS.Agent.Overview;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

public sealed class NetworkOverviewParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_and_MCP_overviews_use_the_same_transport_rows_and_partial_failures(bool refusedPorts)
    {
        var nms = new FakeLibreNms();
        foreach (var (route, file) in new[] { ("devices", "devices"), ("faults", "faults"), ("ports", "ports"), ("system", "system") })
            nms.Transport.On(route, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetworkOverview", file + ".json")));
        if (refusedPorts) nms.Transport.Fails("ports", HttpStatusCode.Forbidden);
        using var host = HostNetworkOverview.Create(nms.Client, nms.Time, true);
        var typed = (await host.ReadAsync()).Result!;
        var tool = new NetworkTools(nms.Client, nms.Fleet, nms.Directory, nms.Resolver, nms.Scope, nms.Runner, nms.Options, nms.Time);
        var data = FakeLibreNms.Parse(await tool.NetworkOverview()).GetProperty("data");
        Assert.Equal(4, typed.Devices.Total);
        Assert.Equal(3, typed.Devices.Up);
        Assert.Equal(1, typed.Devices.Down);
        Assert.Equal(1, typed.Alerts!.AlertGroups);
        Assert.Equal(3, typed.Alerts.Faults);
        Assert.Equal(2, Assert.Single(typed.DownDevices).DeviceId);
        Assert.Equal("Core site", Assert.Single(typed.DownLocations).Location);
        Assert.Equal(typed.Devices.Total, data.GetProperty("devices").GetProperty("total").GetInt32());
        Assert.Equal(typed.Alerts.Faults, data.GetProperty("alerts").GetProperty("faults").GetInt32());
        Assert.Equal(typed.AlertGroups[0].RuleId, data.GetProperty("alert_groups")[0].GetProperty("rule_id").GetInt32());
        Assert.Equal("26.10.0", typed.LibreNmsVersion);
        if (refusedPorts)
        {
            Assert.Null(typed.Ports);
            Assert.Equal(HostFailure.Refused, Assert.Single(typed.Unavailable).Code);
            Assert.Equal("ports", data.GetProperty("unavailable")[0].GetProperty("source").GetString());
        }
        else
        {
            Assert.Empty(typed.Unavailable);
            Assert.Equal(new OverviewPortCounts(1, 1, 1), typed.Ports);
            Assert.Equal(typed.Ports!.Total, data.GetProperty("ports").GetProperty("total").GetInt32());
        }
    }
}
