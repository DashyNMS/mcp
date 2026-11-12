using System.Net;
using DashyNMS.Agent;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;
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
        var routes = new Dictionary<string, Answer>();
        foreach (var name in new[] { "devices", "faults", "ports", "system" })
            routes[name] = new(200, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetworkOverview", name + ".json")));
        if (refusedPorts) routes["ports"] = new(403, "password=synthetic-secret");
        using var server = new FixtureServer(path => routes.GetValueOrDefault(path.Split('?')[0]));
        using var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        var client = provider.GetRequiredService<ILibreNmsClient>();
        client.Connect(new LibreNmsConnection(server.Root, FixtureServer.Token));
        using var host = HostNetworkOverview.Create(client, TimeProvider.System, true);
        var typed = (await host.ReadAsync()).Result!;
        var tool = ActivatorUtilities.CreateInstance<NetworkTools>(provider);
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
