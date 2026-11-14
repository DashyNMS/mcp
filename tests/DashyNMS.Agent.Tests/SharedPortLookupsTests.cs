using System.Text.Json;
using DashyNMS.Agent.Ports;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

public sealed class SharedPortLookupsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PortLookups");
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
    [InlineData("port")]
    [InlineData("neighbours")]
    [InlineData("reverse")]
    [InlineData("trace")]
    public async Task Typed_host_and_MCP_match_over_HTTP(string mode)
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var operation = HostPortLookups.Create(host.GetRequiredService<ILibreNmsClient>(), host.GetRequiredService<ILibreNmsTransport>(), TimeProvider.System, new());
        JsonElement expected;
        JsonElement actual;
        string summary;
        switch (mode)
        {
            case "port":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<PortTools>(mcp).PortStatus("1", "1/1/1", CancellationToken.None));
                var port = (await operation.ReadStatusAsync(new(1, "1/1/1"))).Result!;
                Assert.Equal(101, port.Data.Port.PortId);
                Assert.Equal(10000, port.Data.Port.SpeedMbps);
                Assert.Equal(100, port.Data.Port.InMbps);
                actual = JsonSerializer.SerializeToElement(port.Data, ToolResult.Json);
                summary = port.Summary;
                break;
            case "neighbours":
            case "reverse":
                var id = mode == "reverse" ? 2 : 1;
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<DetailTools>(mcp).DeviceNeighbours(id.ToString()));
                var neighbours = (await operation.ReadNeighboursAsync(new(id))).Result!;
                Assert.Equal(mode == "reverse" ? 1 : 2, neighbours.Data.Neighbours.Count);
                if (mode == "reverse") Assert.Equal("neighbour", neighbours.Data.Neighbours[0].SeenFrom);
                else Assert.Contains(neighbours.Data.Neighbours, n => n.RemoteDeviceId == 2);
                actual = JsonSerializer.SerializeToElement(neighbours.Data, ToolResult.Json);
                summary = neighbours.Summary;
                break;
            default:
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<TraceTools>(mcp).TraceEndpoint("aa:bb:cc:00:00:05"));
                var trace = (await operation.TraceAsync(new("aa:bb:cc:00:00:05"))).Result!;
                Assert.Equal(107, trace.Data.Edge!.PortId);
                Assert.Equal(1, trace.Data.Edge.DeviceId);
                Assert.Equal(1, trace.Data.Edge.MacsOnPort);
                Assert.False(trace.Data.Ambiguous);
                Assert.Single(trace.Observations);
                actual = JsonSerializer.SerializeToElement(trace.Data, ToolResult.Json);
                summary = trace.Summary;
                break;
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), actual));
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
    }

    [Theory]
    [InlineData("resources/ip/arp/aa:bb:cc:00:00:05", "arp")]
    [InlineData("resources/vlans", "vlans")]
    public async Task Trace_preserves_optional_source_failures_in_host_and_MCP(string path, string source)
    {
        var routes = Routes();
        routes[path] = new(403, "{}");
        using var server = new FixtureServer(p => routes.GetValueOrDefault(p));
        using var provider = Connected(server.Root);
        using var host = HostPortLookups.Create(provider.GetRequiredService<ILibreNmsClient>(), provider.GetRequiredService<ILibreNmsTransport>(), TimeProvider.System, new());
        var trace = (await host.TraceAsync(new("aa:bb:cc:00:00:05"))).Result!;
        Assert.True(trace.Data.Found);
        Assert.Contains(trace.Data.Unavailable!, u => u.Source == source);
        Assert.Contains(Assert.Single(trace.Observations).Unavailable, u => u.Source == source);
        var mcp = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<TraceTools>(provider).TraceEndpoint("aa:bb:cc:00:00:05"));
        Assert.Contains(mcp.GetProperty("data").GetProperty("unavailable").EnumerateArray(), u => u.GetProperty("source").GetString() == source);
    }

    [Fact]
    public async Task Multi_MAC_trace_retains_link_failures_in_the_MCP_projection()
    {
        var routes = Routes();
        routes["resources/ip/arp/192.0.2.50"] = new(200, """{"status":"ok","arp":[{"mac_address":"aabbcc000005"},{"mac_address":"aabbcc000006"}]}""");
        routes["resources/fdb/aabbcc000006"] = routes["resources/fdb/aabbcc000005"];
        routes["resources/links"] = new(403, "{}");
        using var server = new FixtureServer(p => routes.GetValueOrDefault(p));
        using var provider = Connected(server.Root);
        using var host = HostPortLookups.Create(provider.GetRequiredService<ILibreNmsClient>(), provider.GetRequiredService<ILibreNmsTransport>(), TimeProvider.System, new());
        var trace = (await host.TraceAsync(new("192.0.2.50"))).Result!;
        Assert.Equal(2, trace.Data.Traces!.Count);
        Assert.Equal("links", Assert.Single(trace.Data.Unavailable!).Source);
        var mcp = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<TraceTools>(provider).TraceEndpoint("192.0.2.50"));
        Assert.Equal(2, mcp.GetProperty("data").GetProperty("traces").GetArrayLength());
        Assert.Equal("links", Assert.Single(mcp.GetProperty("data").GetProperty("unavailable").EnumerateArray()).GetProperty("source").GetString());
    }

    [Fact]
    public async Task Invalid_requests_make_no_reads_and_retirement_cancels_a_pending_port_read()
    {
        var client = Substitute.For<ILibreNmsClient>();
        var pending = new TaskCompletionSource<IReadOnlyList<Port>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Device { DeviceId = 1 } });
        client.Ports.ListForDeviceAsync(1, Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var host = HostPortLookups.Create(client, Substitute.For<ILibreNmsTransport>(), TimeProvider.System, new());
        Assert.Equal(HostFailure.InvalidRequest, (await host.ReadStatusAsync(new(0, "1"))).Error);
        Assert.Equal(HostFailure.InvalidRequest, (await host.TraceAsync(new("not an address"))).Error);
        await client.Devices.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
        var read = host.ReadStatusAsync(new(1, "1"));
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        pending.SetResult(new[] { new Port { PortId = 1, DeviceId = 1 } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadNeighboursAsync(new(1)));
    }
}
