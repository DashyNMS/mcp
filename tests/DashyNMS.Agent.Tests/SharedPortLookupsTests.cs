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
