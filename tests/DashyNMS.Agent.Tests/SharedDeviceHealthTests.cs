using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

public sealed class SharedDeviceHealthTests
{
    [Theory]
    [InlineData("{\"status\":\"ok\",\"devices\":[]}")]
    [InlineData("{\"status\":\"ok\",\"devices\":[{\"device_id\":99,\"hostname\":\"other.example.net\",\"os\":\"wrong\"}]}")]
    public async Task Missing_or_mismatched_fresh_device_uses_only_the_listed_device(string response)
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("devices/1", response);
        using var health = HostDeviceHealth.Create(nms.Client, nms.Time, nms.Options);
        var data = (await health.ReadAsync(new(1))).Result!.Data;
        Assert.Equal(1, data.Device.DeviceId);
        Assert.NotEqual("wrong", data.Device.Os);
        Assert.Contains(data.Unavailable!, u => u.Source == "device");
    }

    [Fact]
    public async Task Recovered_alerts_never_form_open_groups_or_inflate_live_groups()
    {
        var nms = new FakeLibreNms();
        var alerts = Substitute.For<IAlertsApi>();
        nms.Client.Alerts.Returns(alerts);
        alerts.ListAsync(DesktopNMS.Core.Api.AlertQuery.Open, Arg.Any<CancellationToken>()).Returns(
            new DesktopNMS.Core.Models.Alert[]
            {
                new() { Id = 12, DeviceId = 1, RuleId = 3, StateValue = 1 },
                new() { Id = 13, DeviceId = 1, RuleId = 3, StateValue = 0 },
                new() { Id = 14, DeviceId = 1, RuleId = 4, StateValue = 0 },
            });
        using var health = HostDeviceHealth.Create(nms.Client, nms.Time, nms.Options);
        var result = (await health.ReadAsync(new(1))).Result!;
        var group = Assert.Single(result.AlertGroups);
        Assert.Equal(3, group.RuleId);
        Assert.Equal(1, group.FaultCount);
        Assert.Equal(12, Assert.Single(result.Data.OpenAlerts).AlertId);
    }

    [Fact]
    public async Task Complete_health_matches_MCP_over_loopback_HTTP_with_failed_optional_sources()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceSummary");
        var routes = new Dictionary<string, Answer>
        {
            ["devices"] = new(200, File.ReadAllText(Path.Combine(fixtures, "devices.json"))),
            ["devices/1"] = new(200, File.ReadAllText(Path.Combine(fixtures, "device-1.json"))),
        };
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        using var host = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        mcp.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(mcpServer.Root, FixtureServer.Token));
        host.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(hostServer.Root, FixtureServer.Token));
        using var health = HostDeviceHealth.Create(host.GetRequiredService<ILibreNmsClient>(), TimeProvider.System, new());
        var expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<DeviceTools>(mcp).DeviceHealth("1", CancellationToken.None));
        var actual = (await health.ReadAsync(new(1))).Result!;
        Assert.Equal("ios", actual.Data.Device.Os);
        Assert.Equal(864000, actual.Data.UptimeSeconds);
        Assert.Equal(expected.GetProperty("summary").GetString(), actual.Summary);
        var data = JsonSerializer.SerializeToElement(actual.Data, ToolResult.Json);
        foreach (var property in expected.GetProperty("data").EnumerateObject().Where(p => p.Name != "unavailable"))
            Assert.True(JsonElement.DeepEquals(property.Value, data.GetProperty(property.Name)), property.Name);
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
        Assert.NotEmpty(actual.Data.Unavailable!);
        Assert.All(actual.Data.Unavailable!, u => Assert.Equal("This source could not be read.", u.Message));
    }

    [Fact]
    public async Task Shared_health_preserves_MCP_fields_and_explicit_source_failures()
    {
        var mcp = new FakeLibreNms();
        var host = new FakeLibreNms();
        var tools = new DeviceTools(mcp.Client, mcp.Directory, mcp.Resolver, mcp.Scope, mcp.Runner, mcp.Options);
        var expected = FakeLibreNms.Parse(await tools.DeviceHealth("1", CancellationToken.None));
        using var health = HostDeviceHealth.Create(host.Client, host.Time, host.Options);
        var result = (await health.ReadAsync(new(1))).Result!;
        var actual = JsonSerializer.SerializeToElement(result.Data, ToolResult.Json);
        foreach (var property in expected.GetProperty("data").EnumerateObject().Where(p => p.Name != "unavailable"))
            Assert.True(JsonElement.DeepEquals(property.Value, actual.GetProperty(property.Name)), property.Name);
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        var unavailable = Assert.Single(result.Data.Unavailable!);
        Assert.Equal("storage", unavailable.Source);
        Assert.Equal("denied", unavailable.Reason);
        Assert.Equal("This account isn't allowed to read this source.", unavailable.Message);
        Assert.Equal(mcp.Transport.Requests.Order(), host.Transport.Requests.Order());
        Assert.Equal(2, result.AlertGroups.Count);
    }

    [Fact]
    public async Task Retired_host_refuses_cached_reads()
    {
        var nms = new FakeLibreNms();
        using var health = HostDeviceHealth.Create(nms.Client, nms.Time, nms.Options);
        Assert.NotNull((await health.ReadAsync(new(1))).Result);
        var requests = nms.Transport.Requests.Count;
        health.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.ReadAsync(new(1)));
        Assert.Equal(requests, nms.Transport.Requests.Count);
    }
}
