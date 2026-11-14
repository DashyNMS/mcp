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
            new Device { DeviceId = 1, LocationId = 3, Location = "  Site A  ", Status = false },
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
}
