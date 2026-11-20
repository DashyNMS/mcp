using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Tests;

public sealed class SharedGroupsAndLocationsTests
{
    [Fact]
    public async Task Host_and_MCP_share_counts_names_and_requests()
    {
        var mcp = new FakeLibreNms();
        var host = new FakeLibreNms();
        var expected = FakeLibreNms.Parse(await new ScopeTools(mcp.Directory, mcp.Scope, mcp.Runner).ListGroupsAndLocations());
        using var operation = HostGroupsAndLocations.Create(host.Client, host.Time);
        var result = (await operation.ReadAsync()).Result!;
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), JsonSerializer.SerializeToElement(result.Data, ToolResult.Json)));
        Assert.Equal(mcp.Transport.Requests.Order(), host.Transport.Requests.Order());
    }

    [Fact]
    public async Task Exact_filter_names_stay_out_of_serialised_model_data()
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Device { DeviceId = 1, LocationId = 3, Location = "  Site A  ", Status = false, Disabled = false, Ignore = false },
        });
        client.DeviceGroups.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new DeviceGroup { Id = 7, Name = "password=synthetic-secret", Type = "static" },
        });
        using var host = HostGroupsAndLocations.Create(client, TimeProvider.System);
        var result = (await host.ReadAsync()).Result!;
        var location = Assert.Single(result.Data.Locations);
        Assert.Equal("  Site A  ", location.FilterName);
        Assert.Equal("Site A", location.Name);
        Assert.Equal(1, location.Devices);
        Assert.Equal(1, location.Down);
        Assert.Equal(3, location.LocationId);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(result.Data, ToolResult.Json));
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadAsync());
    }

    /// <summary>
    /// A location's down count holds only devices LibreNMS reported down; ignored devices and
    /// those with no reported state are counted apart, in MCP's JSON as well (#121).
    /// </summary>
    [Fact]
    public async Task Location_counts_keep_ignored_and_unknown_devices_out_of_down()
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("devices", """
            {"status":"ok","devices":[
              {"device_id":1,"hostname":"down.example.net","location":"Lab","status":0,"disabled":0,"ignore":0},
              {"device_id":2,"hostname":"ignored-down.example.net","location":"Lab","status":0,"disabled":0,"ignore":1},
              {"device_id":3,"hostname":"no-status.example.net","location":"Lab","disabled":0,"ignore":0},
              {"device_id":4,"hostname":"no-flags.example.net","location":"Lab"},
              {"device_id":5,"hostname":"up.example.net","location":"Lab","status":1,"disabled":0,"ignore":0}
            ]}
            """);
        var root = FakeLibreNms.Parse(await new ScopeTools(nms.Directory, nms.Scope, nms.Runner).ListGroupsAndLocations());
        var lab = Assert.Single(root.GetProperty("data").GetProperty("locations").EnumerateArray());

        Assert.Equal(5, lab.GetProperty("devices").GetInt32());
        Assert.Equal(1, lab.GetProperty("down").GetInt32());
        Assert.Equal(1, lab.GetProperty("ignored").GetInt32());
        Assert.Equal(2, lab.GetProperty("unknown").GetInt32());
    }
}
