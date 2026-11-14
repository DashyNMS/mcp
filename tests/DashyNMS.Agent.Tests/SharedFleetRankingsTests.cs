using System.Text.Json;
using DashyNMS.Agent.Health;
using DashyNMS.Agent.Rankings;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

public sealed class SharedFleetRankingsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FleetRankings");
    private static Dictionary<string, Answer> Routes()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "routes.json")));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
    }
    private static ServiceProvider Connected(Uri root)
    {
        var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(root, FixtureServer.Token));
        return provider;
    }

    [Theory]
    [InlineData("traffic", null)]
    [InlineData("errors", null)]
    [InlineData("errors", "Core switches")]
    [InlineData("devices", null)]
    [InlineData("out", null)]
    [InlineData("problems", null)]
    [InlineData("health", null)]
    public async Task Fleet_reads_match_independent_facts_and_MCP_over_HTTP(string mode, string? group)
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        var network = ActivatorUtilities.CreateInstance<NetworkTools>(mcp);
        using var rankings = HostFleetRankings.Create(host.GetRequiredService<ILibreNmsClient>(), TimeProvider.System);
        JsonElement expected;
        JsonElement actual;
        string summary;
        if (mode == "health")
        {
            using var health = HostHealthOverview.Create(host.GetRequiredService<ILibreNmsClient>(), host.GetRequiredService<ILibreNmsTransport>(), TimeProvider.System, new());
            var result = (await health.ReadAsync(new())).Result!;
            Assert.Equal(9, result.Totals.Readings);
            Assert.Equal(3, result.Totals.Critical);
            Assert.Equal(0, result.Totals.Warning);
            Assert.Equal(3, result.Data.Readings.Count);
            Assert.Equal(0, result.DeniedDevices);
            expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<HealthTools>(mcp).HealthOverview());
            actual = JsonSerializer.SerializeToElement(result.Data, ToolResult.Json);
            summary = result.Summary;
        }
        else if (mode is "traffic" or "errors")
        {
            var result = (await rankings.ReadPortsAsync(new(Errors: mode == "errors", Group: group))).Result!;
            Assert.Equal(group is null ? (mode == "traffic" ? new[] { 101, 249, 108 } : new[] { 108, 249 }) : new[] { 108 }, result.Data.Ports.Select(p => p.PortId));
            expected = FakeLibreNms.Parse(mode == "traffic" ? await network.TopInterfaces(group: group) : await network.TopErrors(group: group));
            actual = JsonSerializer.SerializeToElement(result.Data, ToolResult.Json);
            summary = result.Summary;
        }
        else
        {
            var by = mode == "devices" ? null : mode;
            var result = (await rankings.ReadDevicesAsync(new(By: by))).Result!;
            Assert.Equal(mode == "out" ? new[] { 2, 1 } : new[] { 1, 2 }, result.Data.Devices.Select(d => d.Device!.DeviceId));
            expected = FakeLibreNms.Parse(await network.TopDevices(by));
            actual = JsonSerializer.SerializeToElement(result.Data, ToolResult.Json);
            summary = result.Summary;
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), actual));
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
    }

    [Fact]
    public async Task Bounded_ranking_retains_total_candidates_and_retirement_refuses_cached_reads()
    {
        var nms = new FakeLibreNms();
        using var host = HostFleetRankings.Create(nms.Client, nms.Time);
        var result = (await host.ReadPortsAsync(new(Limit: 1))).Result!;
        Assert.Single(result.Data.Ports);
        Assert.Equal(3, result.CandidatesTotal);
        Assert.Equal(4, result.PortsChecked);
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadDevicesAsync(new()));
    }
}
