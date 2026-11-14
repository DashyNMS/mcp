using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The shared device resolution fixture served over loopback HTTP to MCP's own wiring (its
/// <c>device_health</c> tool, which resolves its <c>device</c> argument first, over Core's real
/// LibreNmsClient) and to <see cref="HostDeviceResolver"/> over the same kind of client.
/// DashyNMS/mobile serves the same files to its Intelligence device tools.
/// </summary>
/// <remarks>
/// MCP reports anything but a match as the sentence a model reads, with ambiguous candidates under
/// details; the host gets the same tiers as <see cref="ResolvedMatch"/> outcomes. Both must agree on
/// which device, which candidates and how many were left out, and MCP's sentences must be unchanged.
/// </remarks>
public sealed class SharedDeviceResolutionHttpFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceResolution");

    private static JsonElement Load(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, file)));
        return json.RootElement.Clone();
    }

    private static readonly JsonElement Cases = Load("cases.json");
    private static readonly JsonElement Transport = Load("transport.json");

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] Strings(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(r => r.GetString()!).ToArray();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> TransportFailures()
    {
        var data = new TheoryData<string>();
        foreach (var f in Transport.GetProperty("failures").EnumerateArray())
        {
            data.Add(f.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static Dictionary<string, Answer> Routes()
    {
        var routes = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var route in Cases.GetProperty("routes").EnumerateObject())
        {
            routes[route.Name] = new(200, File.ReadAllText(Path.Combine(FixtureDirectory, route.Value.GetString()!)));
        }

        return routes;
    }

    private static ServiceProvider Connected(Uri root)
    {
        var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(
            new LibreNmsConnection(root, FixtureServer.Token, timeoutSeconds: Transport.GetProperty("timeoutSeconds").GetInt32()));
        return provider;
    }

    private static DeviceTools Tools(IServiceProvider provider) => ActivatorUtilities.CreateInstance<DeviceTools>(provider);

    private static HostDeviceResolver Host(IServiceProvider provider)
        => HostDeviceResolver.Create(provider.GetRequiredService<ILibreNmsClient>(), TimeProvider.System, serverTimestampsAreUtc: true);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Mcp_and_the_host_resolve_the_fixture_identically_over_HTTP(string name)
    {
        var c = Cases.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var resolver = Host(host);
        var phrase = c.GetProperty("device").GetString()!;

        var root = FakeLibreNms.Parse(await Tools(mcp).DeviceHealth(phrase, CancellationToken.None));
        var outcome = await resolver.ResolveAsync(new DeviceResolutionRequest(phrase));

        // Resolution reads the device list once and nothing else.
        Assert.Equal(Strings(c, "requests"), hostServer.Requests);
        Assert.DoesNotContain("synthetic-secret", root.GetRawText(), StringComparison.Ordinal);

        if (Text(c, "code") is { } code)
        {
            Assert.Null(outcome.Result);
            Assert.Equal(Enum.Parse<HostFailure>(code), outcome.Error!.Code);
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.Equal(Text(c, "mcp"), root.GetProperty("message").GetString());
            Assert.Empty(mcpServer.Requests);
            return;
        }

        Assert.Null(outcome.Error);
        var result = outcome.Result!;
        Assert.Equal(Enum.Parse<ResolvedMatch>(Text(c, "match")!), result.Match);

        switch (result.Match)
        {
            case ResolvedMatch.Found:
                var id = c.GetProperty("id").GetInt32();
                Assert.Equal("ok", root.GetProperty("status").GetString());
                Assert.Equal(id, root.GetProperty("data").GetProperty("device").GetProperty("device_id").GetInt32());
                Assert.Equal(id, result.Device!.DeviceId);
                Assert.Equal(Text(c, "state"), result.Device.State.ToString());
                Assert.Empty(result.Candidates);
                Assert.Equal(0, result.Omitted);
                break;

            case ResolvedMatch.Ambiguous:
                var expected = c.GetProperty("candidates").EnumerateArray().ToList();
                var listed = root.GetProperty("details").GetProperty("candidates").EnumerateArray().ToList();
                Assert.Equal("error", root.GetProperty("status").GetString());
                Assert.Equal(Text(c, "mcp"), root.GetProperty("message").GetString());
                Assert.Equal(c.GetProperty("omitted").GetInt32(), root.GetProperty("details").GetProperty("omitted").GetInt32());
                Assert.Equal(c.GetProperty("omitted").GetInt32(), result.Omitted);
                Assert.Null(result.Device);
                Assert.Equal(expected.Count, listed.Count);
                Assert.Equal(expected.Count, result.Candidates.Count);
                for (var i = 0; i < expected.Count; i++)
                {
                    var want = new DeviceCandidate(
                        expected[i].GetProperty("deviceId").GetInt32(), Text(expected[i], "hostname"), Text(expected[i], "display"), Text(expected[i], "ip"));
                    Assert.Equal(want, result.Candidates[i]);
                    Assert.Equal(want, new DeviceCandidate(
                        listed[i].GetProperty("device_id").GetInt32(), Text(listed[i], "hostname"), Text(listed[i], "display"), Text(listed[i], "ip")));
                }

                break;

            default:
                Assert.Equal("error", root.GetProperty("status").GetString());
                Assert.Equal(Text(c, "mcp"), root.GetProperty("message").GetString());
                Assert.False(root.TryGetProperty("details", out _));
                Assert.Null(result.Device);
                Assert.Empty(result.Candidates);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task The_host_fails_with_a_fixed_category_when_the_device_list_fails(string name)
    {
        var failure = Transport.GetProperty("failures").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        var phrase = Transport.GetProperty("device").GetString()!;
        var failing = Text(failure, "behaviour") == "hang" ? Answer.Hang
            : new Answer(failure.GetProperty("status").GetInt32(),
                Transport.GetProperty("bodies").GetProperty(failure.GetProperty("body").GetString()!).GetString()!);
        using var mcpServer = new FixtureServer(p => p == "devices" ? failing : null);
        using var hostServer = new FixtureServer(p => p == "devices" ? failing : null);
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var resolver = Host(host);

        // Side by side: a request that never gets an answer costs the full timeout on each.
        var mcpCall = Tools(mcp).DeviceHealth(phrase, CancellationToken.None);
        var hostCall = resolver.ResolveAsync(new DeviceResolutionRequest(phrase));
        await Task.WhenAll(mcpCall, hostCall);
        var text = await mcpCall;
        var outcome = await hostCall;

        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<HostFailure>(failure.GetProperty("code").GetString()!), outcome.Error!.Code);
        Assert.DoesNotContain("synthetic-secret", outcome.Error.Message, StringComparison.Ordinal);
        Assert.Equal(Strings(failure, "requests"), hostServer.Requests);

        // MCP: an error, never "no match", and never the server's secret.
        var root = FakeLibreNms.Parse(text);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.NotEqual(Text(Cases.GetProperty("cases").EnumerateArray().First(x => Text(x, "match") == "NoMatch"), "mcp"), root.GetProperty("message").GetString());
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        Assert.Equal(Strings(failure, "requests"), mcpServer.Requests);
    }
}
