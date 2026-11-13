using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The shared device summary fixture served over loopback HTTP to the MCP server's own wiring (its
/// device_health tool over Core's real LibreNmsClient) and to <see cref="HostDeviceSummary"/> over the
/// same kind of client. DashyNMS/mobile serves the same files to its Intelligence device_summary.
/// </summary>
/// <remarks>
/// <c>device_health</c> reads more than the summary (maintenance, alerts, health, ports); the fixture
/// answers none of those, so only its device and uptime are compared. The two adapters differ on
/// purpose when <c>devices/{id}</c> fails: <c>device_health</c> answers from the device list it resolved
/// from and names the device read as unavailable, while the host summary fails, because it never
/// reports a list row as the device it was asked for.
/// </remarks>
public sealed class SharedDeviceSummaryHttpFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceSummary");

    private static JsonElement Load(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, file)));
        return json.RootElement.Clone();
    }

    private static readonly JsonElement Cases = Load("cases.json");
    private static readonly JsonElement Transport = Load("transport.json");

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

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

    /// <summary>The routes from <c>cases.json</c>, as LibreNMS would answer them; any other path is a 404.</summary>
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

    /// <summary>device_health as the MCP SDK activates it from the server's services.</summary>
    private static DeviceTools Tools(IServiceProvider provider) => ActivatorUtilities.CreateInstance<DeviceTools>(provider);

    private static HostDeviceSummary Host(IServiceProvider provider)
        => HostDeviceSummary.Create(provider.GetRequiredService<ILibreNmsClient>(), serverTimestampsAreUtc: true);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Device_health_and_the_host_summary_read_the_fixture_identically_over_HTTP(string name)
    {
        var c = Cases.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var summary = Host(host);
        var id = c.GetProperty("device").GetInt32();

        var root = FakeLibreNms.Parse(await Tools(mcp).DeviceHealth(id.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
        var outcome = await summary.ReadAsync(new DeviceSummaryRequest(id));

        // The host summary makes exactly one read; device_health makes its own others besides.
        Assert.Equal(Strings(c, "requests"), hostServer.Requests);

        if (Text(c, "code") is { } code)
        {
            Assert.Null(outcome.Result);
            Assert.Equal(Enum.Parse<HostFailure>(code), outcome.Error!.Code);
            if (Text(c, "mcp") == "list")
            {
                // Listed, so device_health answers from the list row; the summary won't.
                Assert.Equal("ok", root.GetProperty("status").GetString());
                Assert.Equal(id, root.GetProperty("data").GetProperty("device").GetProperty("device_id").GetInt32());
                Assert.Contains("devices/" + id.ToString(CultureInfo.InvariantCulture), mcpServer.Requests);
            }
            else
            {
                Assert.Equal("error", root.GetProperty("status").GetString());
            }

            return;
        }

        Assert.Contains("devices/" + id.ToString(CultureInfo.InvariantCulture), mcpServer.Requests);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        var data = root.GetProperty("data");
        var device = data.GetProperty("device");
        var result = outcome.Result!;
        Assert.Null(outcome.Error);
        var expected = c.GetProperty("expected");

        void Same(string field, string json, string? hostValue)
        {
            Assert.Equal(Text(expected, field), Text(device, json));
            Assert.Equal(Text(expected, field), hostValue);
        }

        Assert.Equal(id, device.GetProperty("device_id").GetInt32());
        Assert.Equal(id, result.Device.DeviceId);
        Same("hostname", "hostname", result.Device.Hostname);
        Same("display", "display", result.Device.Display);
        Same("sysName", "sys_name", result.Device.SysName);
        Same("ip", "ip", result.Device.Ip);
        Same("os", "os", result.Device.Os);
        Same("hardware", "hardware", result.Device.Hardware);
        Same("location", "location", result.Device.Location);
        Same("status", "status", result.Device.Status);
        Assert.Equal(Text(expected, "state"), result.Device.State.ToString());
        Assert.Equal(Number(expected, "uptimeSeconds"), Number(data, "uptime_seconds"));
        Assert.Equal(Number(expected, "uptimeSeconds"), result.UptimeSeconds);

        // The location's id and its two names exist only on the typed result: MCP's JSON is unchanged.
        Assert.Equal(Number(expected, "locationId"), (long?)result.Location?.Id);
        Assert.Equal(Text(expected, "locationName"), result.Location?.Name);
        Assert.Equal(Text(expected, "filterName"), result.Location?.FilterName);
        Assert.DoesNotContain("synthetic-secret", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", result.Device.Hardware ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task The_host_summary_fails_with_a_fixed_category_when_the_device_read_fails(string name)
    {
        var failure = Transport.GetProperty("failures").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        var id = Transport.GetProperty("device").GetInt32();
        var path = "devices/" + id.ToString(CultureInfo.InvariantCulture);
        var routes = Routes();
        var failing = Text(failure, "behaviour") == "hang" ? Answer.Hang
            : new Answer(failure.GetProperty("status").GetInt32(),
                Transport.GetProperty("bodies").GetProperty(failure.GetProperty("body").GetString()!).GetString()!);
        using var mcpServer = new FixtureServer(p => p == path ? failing : routes.GetValueOrDefault(p));
        using var hostServer = new FixtureServer(p => p == path ? failing : routes.GetValueOrDefault(p));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var summary = Host(host);

        // Side by side: a request that never gets an answer costs the full timeout on each.
        var mcpCall = Tools(mcp).DeviceHealth(id.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
        var hostCall = summary.ReadAsync(new DeviceSummaryRequest(id));
        await Task.WhenAll(mcpCall, hostCall);
        var text = await mcpCall;
        var outcome = await hostCall;

        // Host: the shared category, no result and no server text.
        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<HostFailure>(failure.GetProperty("code").GetString()!), outcome.Error!.Code);
        Assert.DoesNotContain("synthetic-secret", outcome.Error.Message, StringComparison.Ordinal);
        Assert.Equal(Strings(failure, "requests"), hostServer.Requests);

        // MCP: device_health still answers from the device list and names the device read unavailable.
        var root = FakeLibreNms.Parse(text);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(id, root.GetProperty("data").GetProperty("device").GetProperty("device_id").GetInt32());
        Assert.Contains(root.GetProperty("data").GetProperty("unavailable").EnumerateArray(), u => u.GetProperty("source").GetString() == "device");
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        Assert.Equal(Strings(failure, "requests").Length, mcpServer.Requests.Count(r => r == path));
    }
}
