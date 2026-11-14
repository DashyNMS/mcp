using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

public sealed class SharedDeviceDetailsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceDetails");
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
    [InlineData(null)]
    [InlineData("all")]
    [InlineData("inventory")]
    [InlineData("mac")]
    [InlineData("bgp")]
    public async Task Sections_match_independent_facts_and_MCP_over_HTTP(string? section)
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var details = HostDeviceDetails.Create(host.GetRequiredService<ILibreNmsClient>(), host.GetRequiredService<ILibreNmsTransport>(), TimeProvider.System);
        string[]? sections = section is null ? null : [section];
        var result = (await details.ReadAsync(new(1, sections))).Result!;
        var expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<DetailTools>(mcp).DeviceDetails("1", sections));
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), JsonSerializer.SerializeToElement(result.Data, ToolResult.Json)));
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
        if (section is null or "all")
        {
            Assert.Equal("SG00TEST01", result.Data.Inventory![0].Serial);
            Assert.Equal(2, result.SectionTotals["inventory"]);
            Assert.Equal(new[] { 1, 120 }, result.Data.Vlans!.Select(v => v.Vlan));
            Assert.Equal(370, result.Data.Poe!.Budgets![0].TotalWatts);
            Assert.Equal(120.5, result.Data.Poe.Budgets[0].UsedWatts);
            Assert.Equal("idle", result.Data.Routing!.BgpSessions[0].State);
            Assert.Equal(99.941, result.Data.Availability![1].Percent);
            Assert.Equal("192.0.2.1/24", result.Data.Addresses![0].Address);
            Assert.Empty(result.Data.Wireless!);
            Assert.Null(result.Data.Unavailable);
        }
        if (section is null) Assert.Null(result.Data.Fdb);
        if (section == "all") Assert.Equal("aa:bb:cc:dd:01:07", Assert.Single(result.Data.Fdb!).Mac);
    }

    [Fact]
    public async Task Paging_counts_original_rows_and_refused_inventory_preserves_other_sections()
    {
        var nms = new FakeLibreNms();
        using var host = HostDeviceDetails.Create(nms.Client, nms.Transport, nms.Time);
        var result = (await host.ReadAsync(new(1, ["inventory"], Offset: 1, Limit: 1))).Result!;
        Assert.Single(result.Data.Inventory!);
        Assert.Equal(2, result.SectionTotals["inventory"]);
        Assert.Equal(2, result.Data.Pages!["inventory"].Total);
        nms.Transport.Fails("inventory/1/all", System.Net.HttpStatusCode.Forbidden);
        result = (await host.ReadAsync(new(1, ["inventory", "availability"]))).Result!;
        Assert.Null(result.Data.Inventory);
        Assert.NotNull(result.Data.Availability);
        Assert.Equal("denied", Assert.Single(result.Data.Unavailable!).Reason);
        Assert.Equal("This account isn't allowed to read this source.", result.Data.Unavailable![0].Message);
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadAsync(new(1)));
    }
}
