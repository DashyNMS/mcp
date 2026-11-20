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

    /// <summary>
    /// Each device is counted once, by the state LibreNMS reported (#121): an ignored device
    /// isn't up or down, one whose response leaves a flag out is unknown, and only devices
    /// reported down reach the down list and the locations with devices down.
    /// </summary>
    [Fact]
    public async Task Unknown_and_ignored_devices_are_counted_apart_from_up_and_down()
    {
        const string devices = """
            {"status":"ok","count":7,"devices":[
              {"device_id":1,"hostname":"up.example.net","status":true,"disabled":false,"ignore":false,"location":"Core site"},
              {"device_id":2,"hostname":"down.example.net","status":false,"disabled":false,"ignore":false,"location":"Core site"},
              {"device_id":3,"hostname":"ignored-up.example.net","status":true,"disabled":false,"ignore":true,"location":"Lab"},
              {"device_id":4,"hostname":"ignored-down.example.net","status":false,"disabled":false,"ignore":true,"location":"Lab"},
              {"device_id":5,"hostname":"no-status.example.net","disabled":false,"ignore":false,"location":"Lab"},
              {"device_id":6,"hostname":"no-flags.example.net","location":"Lab"},
              {"device_id":7,"hostname":"disabled.example.net","status":false,"disabled":true,"ignore":true,"location":"Lab"}
            ]}
            """;
        var routes = new Dictionary<string, Answer>
        {
            ["devices"] = new(200, devices),
            ["faults"] = new(200, """{"status":"ok","alerts":[]}"""),
            ["ports"] = new(200, """{"status":"ok","ports":[]}"""),
            ["system"] = new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        };
        using var server = new FixtureServer(path => routes.GetValueOrDefault(path.Split('?')[0]));
        using var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        var client = provider.GetRequiredService<ILibreNmsClient>();
        client.Connect(new LibreNmsConnection(server.Root, FixtureServer.Token));
        using var host = HostNetworkOverview.Create(client, TimeProvider.System, true);
        var typed = (await host.ReadAsync()).Result!;
        var tool = ActivatorUtilities.CreateInstance<NetworkTools>(provider);
        var root = FakeLibreNms.Parse(await tool.NetworkOverview());
        var counts = root.GetProperty("data").GetProperty("devices");

        Assert.Equal(new OverviewDeviceCounts(7, 1, 1, 1, 2, 2), typed.Devices);
        Assert.Equal(typed.Devices.Total, typed.Devices.Up + typed.Devices.Down + typed.Devices.Disabled + typed.Devices.Ignored + typed.Devices.Unknown);
        Assert.Equal(2, Assert.Single(typed.DownDevices).DeviceId);
        Assert.Equal("down", typed.DownDevices[0].Status);
        Assert.Equal("Core site", Assert.Single(typed.DownLocations).Location);
        Assert.Equal(1, counts.GetProperty("up").GetInt32());
        Assert.Equal(1, counts.GetProperty("down").GetInt32());
        Assert.Equal(1, counts.GetProperty("disabled").GetInt32());
        Assert.Equal(2, counts.GetProperty("ignored").GetInt32());
        Assert.Equal(2, counts.GetProperty("unknown").GetInt32());
        Assert.Equal(2, Assert.Single(root.GetProperty("data").GetProperty("down_devices").EnumerateArray()).GetProperty("device_id").GetInt32());
        Assert.Equal("7 devices visible to this account: 1 up, 1 down, 1 disabled, 2 ignored, 2 with no reported state; 0 open alerts (0 critical, 0 warning).",
            root.GetProperty("summary").GetString());
    }
}
