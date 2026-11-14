using System.Text.Json;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class SharedEventLookupsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EventHistory");
    private static Dictionary<string, Answer> Routes()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "routes.json")));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
    }
    private static ServiceProvider Connected(Uri root)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(FakeLibreNms.Now));
        var provider = services.AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(root, FixtureServer.Token));
        return provider;
    }
    private static HostEventLookups Host(ServiceProvider provider) => HostEventLookups.Create(
        provider.GetRequiredService<ILibreNmsClient>(), provider.GetRequiredService<IUnimusApi>(), provider.GetRequiredService<IGraylogApi>(),
        provider.GetRequiredService<TimeProvider>(), new());

    [Theory]
    [InlineData("device")]
    [InlineData("network")]
    [InlineData("raised")]
    [InlineData("changes")]
    public async Task History_matches_independent_facts_and_MCP_over_HTTP(string mode)
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var operation = Host(host);
        JsonElement expected;
        JsonElement actual;
        string summary;
        switch (mode)
        {
            case "device":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<EventTools>(mcp).DeviceEvents("1"));
                var device = (await operation.ReadDeviceAsync(new(1))).Result!;
                Assert.True(device.Data.Window.WindowComplete);
                Assert.Contains(device.Data.Entries, e => e.Source == "outage");
                Assert.Contains(device.Data.Entries, e => e.Text == "Interface 1/1/7 went down");
                actual = JsonSerializer.SerializeToElement(device.Data, ToolResult.Json);
                summary = device.Summary;
                break;
            case "network":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<NetworkTools>(mcp).NetworkEvents(log: "both"));
                var network = (await operation.ReadNetworkAsync(new(Log: "both"))).Result!;
                Assert.True(network.Data.Window.WindowComplete);
                Assert.Contains(network.Data.Entries, e => e.Source == "eventlog");
                Assert.Contains(network.Data.Entries, e => e.Source == "alertlog");
                actual = JsonSerializer.SerializeToElement(network.Data, ToolResult.Json);
                summary = network.Summary;
                break;
            case "raised":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<AlertTools>(mcp).ActiveAlerts(hours: 24));
                var raised = (await operation.ReadRaisedAlertsAsync(new(Hours: 24))).Result!;
                Assert.Equal(2, raised.Data.Page.Total);
                Assert.Contains(raised.Data.Devices.SelectMany(d => d.Alerts), a => a.RecoveredAt is not null && a.StillOpen == false);
                Assert.Contains(raised.Data.Devices.SelectMany(d => d.Alerts), a => a.StillOpen == true);
                actual = JsonSerializer.SerializeToElement(raised.Data, ToolResult.Json);
                summary = raised.Summary;
                break;
            default:
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<EventTools>(mcp).WhatChanged("1"));
                var changes = (await operation.ReadChangesAsync(new(1))).Result!;
                Assert.False(changes.Data.Unimus.Configured);
                Assert.False(changes.Data.Graylog.Configured);
                Assert.NotEmpty(changes.Data.Librenms.Entries);
                actual = JsonSerializer.SerializeToElement(changes.Data, ToolResult.Json);
                summary = changes.Summary;
                break;
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), actual));
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
    }

    [Fact]
    public async Task A_busy_log_reports_an_incomplete_window_and_host_retirement_refuses_cached_reads()
    {
        var routes = Routes();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "incomplete.json")));
        var count = fixture.RootElement.GetProperty("events").GetInt32();
        var timestamp = fixture.RootElement.GetProperty("timestamp").GetString();
        routes["logs/eventlog"] = new(200, JsonSerializer.Serialize(new
        {
            status = "ok", logs = Enumerable.Range(1, count).Select(i => new { event_id = i, device_id = 1, datetime = timestamp, message = "Synthetic event" }),
        }));
        using var server = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var result = (await host.ReadNetworkAsync(new(Hours: 24, Limit: 1))).Result!;
        Assert.False(result.Data.Window.WindowComplete);
        Assert.NotNull(result.Data.Window.CoveredSince);
        Assert.Contains("start of the window isn't covered", result.Summary, StringComparison.Ordinal);
        Assert.Equal(3, server.Requests.Count(p => p == "logs/eventlog"));
        Assert.Equal(count, result.Data.Page.Total);
        Assert.Single(result.Data.Entries);
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadDeviceAsync(new(1)));
    }
}
