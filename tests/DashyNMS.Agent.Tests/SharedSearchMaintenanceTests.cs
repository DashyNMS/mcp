using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

public sealed class SharedSearchMaintenanceTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(403, null)]
    [InlineData(404, null)]
    public async Task Only_returned_rows_are_checked_and_failed_checks_are_unknown(int secondStatus, bool? expectedMaintenance)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceSearch");
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "maintenance.json")));
        var f = fixture.RootElement;
        var routes = new Dictionary<string, Answer>
        {
            ["devices"] = new(200, File.ReadAllText(Path.Combine(folder, "devices.json"))),
            ["devices/3/maintenance"] = new(200, f.GetProperty("active").GetRawText()),
            ["devices/5/maintenance"] = new(secondStatus, f.GetProperty("inactive").GetRawText()),
        };
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        using var host = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        mcp.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(mcpServer.Root, FixtureServer.Token));
        host.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(hostServer.Root, FixtureServer.Token));
        using var search = HostDeviceSearch.Create(host.GetRequiredService<ILibreNmsClient>(), TimeProvider.System, true);
        var text = await ActivatorUtilities.CreateInstance<DeviceTools>(mcp).FindDevice("sw", limit: 2);
        var result = (await search.FindDevicesAsync(new("sw", Limit: 2))).Result!;
        Assert.Equal(new[] { 3, 5 }, result.Matches.Select(m => m.Device.DeviceId));
        Assert.Equal(DeviceState.Maintenance, result.Matches[0].Device.State);
        Assert.Equal("maintenance", result.Matches[0].Device.Status);
        Assert.True(result.Matches[0].Device.UnderMaintenance);
        Assert.Equal(DeviceState.Up, result.Matches[1].Device.State);
        Assert.Equal(expectedMaintenance, result.Matches[1].Device.UnderMaintenance);
        var rows = FakeLibreNms.Parse(text).GetProperty("data").GetProperty("devices");
        Assert.Equal("maintenance", rows[0].GetProperty("status").GetString());
        Assert.True(rows[0].GetProperty("under_maintenance").GetBoolean());
        if (expectedMaintenance is null) Assert.False(rows[1].TryGetProperty("under_maintenance", out _));
        else Assert.False(rows[1].GetProperty("under_maintenance").GetBoolean());
        var expectedRequests = f.GetProperty("requests").EnumerateArray().Select(r => r.GetString()!).ToArray();
        Assert.Equal(expectedRequests, mcpServer.Requests);
        Assert.Equal(expectedRequests, hostServer.Requests);
    }
}
