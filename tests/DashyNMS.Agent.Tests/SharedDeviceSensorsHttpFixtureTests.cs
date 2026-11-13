using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Health;
using DashyNMS.Agent.Summaries;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The shared device sensors fixture served over loopback HTTP to the MCP server's own wiring (its
/// device_sensors tool over Core's real LibreNmsClient and transport) and to
/// <see cref="HostDeviceSensors"/> over the same kind of client and transport. DashyNMS/mobile
/// serves the same files to its Intelligence device_sensors.
/// </summary>
/// <remarks>
/// <c>sensors.json</c> is the network-wide list: a reading of zero beside a missing one, readings
/// judged by their own limits and by Core's default thresholds, a dBm reading in the "nothing
/// plugged in" band, readings with nothing to judge them by, and another device's sensor that must
/// not appear. The device-by-device server refuses that list (403) and answers the device's own
/// health routes from <c>health.json</c>, which also lists processor and storage types that must
/// never be read. Those routes are read concurrently, so their requests are compared as a set.
/// </remarks>
public sealed class SharedDeviceSensorsHttpFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceSensors");

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

    private static int[] Ints(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(i => i.GetInt32()).ToArray();

    private static double?[] Values(JsonElement element)
        => element.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (double?)null : v.GetDouble()).ToArray();

    /// <summary>Values rounded to two places, compared as numbers: 3.22 may round to 3.2200000000000002.</summary>
    private static void SameValues(double?[] expected, IEnumerable<double?> actual)
    {
        var values = actual.ToArray();
        Assert.Equal(expected.Length, values.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].HasValue, values[i].HasValue);
            if (expected[i] is { } e) Assert.Equal(e, values[i]!.Value, 9);
        }
    }

    private static string[] Sorted(IEnumerable<string> requests) => requests.Order(StringComparer.Ordinal).ToArray();

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

    private static Answer File200(string file) => new(200, File.ReadAllText(Path.Combine(FixtureDirectory, file)));

    /// <summary>One server's routes from <c>cases.json</c>, as LibreNMS would answer them; any other path is a 404.</summary>
    internal static Dictionary<string, Answer> Routes(string server)
    {
        var routes = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var route in Cases.GetProperty("servers").GetProperty(server).EnumerateObject())
        {
            if (route.Value.ValueKind == JsonValueKind.Number)
            {
                routes[route.Name] = new(route.Value.GetInt32(), """{"status":"error","message":"Insufficient permissions"}""");
            }
            else if (route.Name == "health")
            {
                foreach (var health in Load(route.Value.GetString()!).EnumerateObject())
                {
                    routes[health.Name] = new(200, health.Value.GetRawText());
                }
            }
            else
            {
                routes[route.Name] = File200(route.Value.GetString()!);
            }
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

    /// <summary>device_sensors as the MCP SDK activates it from the server's services.</summary>
    private static HealthTools Tools(IServiceProvider provider) => ActivatorUtilities.CreateInstance<HealthTools>(provider);

    private static HostDeviceSensors Host(IServiceProvider provider) => HostDeviceSensors.Create(
        provider.GetRequiredService<ILibreNmsClient>(), provider.GetRequiredService<ILibreNmsTransport>(), TimeProvider.System, serverTimestampsAreUtc: true);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Device_sensors_and_the_host_operation_judge_the_fixture_identically_over_HTTP(string name)
    {
        var c = Cases.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var routes = Routes(c.GetProperty("server").GetString()!);
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var sensors = Host(host);
        var device = c.GetProperty("device").GetInt32();
        var type = Text(c, "type");

        var text = await Tools(mcp).DeviceSensors(device.ToString(CultureInfo.InvariantCulture), type, CancellationToken.None);
        var root = FakeLibreNms.Parse(text);
        var outcome = await sensors.ReadAsync(new(device, type));

        Assert.Equal(Sorted(Strings(c, "requests")), Sorted(mcpServer.Requests));
        Assert.Equal(Sorted(Strings(c, "requests")), Sorted(hostServer.Requests));

        if (Text(c, "code") is { } code)
        {
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.False(root.TryGetProperty("data", out _));
            Assert.Null(outcome.Result);
            Assert.Equal(Enum.Parse<HostFailure>(code), outcome.Error!.Code);
            return;
        }

        Assert.Equal("ok", root.GetProperty("status").GetString());
        var shown = root.GetProperty("data").GetProperty("readings").EnumerateArray().ToList();
        var result = outcome.Result!;
        Assert.Null(outcome.Error);

        Assert.Equal(Ints(c, "readings"), shown.Select(r => r.GetProperty("sensor_id").GetInt32()));
        Assert.Equal(Ints(c, "readings"), result.Readings.Select(r => r.SensorId));
        Assert.Equal(Strings(c, "states"), shown.Select(r => r.GetProperty("state").GetString()));
        Assert.Equal(Strings(c, "states"), result.Readings.Select(r => r.State));
        Assert.Equal(Strings(c, "assessments"), result.Readings.Select(r => r.Assessment.ToString()));
        // A missing reading stays null on both sides, never a zero.
        // MCP leaves a null value out of its JSON.
        SameValues(Values(c), shown.Select(r => r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null));
        SameValues(Values(c), result.Readings.Select(r => r.Value));
        // An unassessed reading is never ok.
        Assert.All(result.Readings.Where(r => r.Assessment is SensorAssessment.NoReading or SensorAssessment.NoLimits), r => Assert.Equal("unknown", r.State));

        var totals = c.GetProperty("totals");
        Assert.Equal(totals.GetProperty("readings").GetInt32(), result.Totals.Readings);
        Assert.Equal(totals.GetProperty("critical").GetInt32(), result.Totals.Critical);
        Assert.Equal(totals.GetProperty("warning").GetInt32(), result.Totals.Warning);
        Assert.Equal(totals.GetProperty("ok").GetInt32(), result.Totals.Ok);
        Assert.Equal(totals.GetProperty("unknown").GetInt32(), result.Totals.Unknown);

        var types = c.GetProperty("types").EnumerateObject().Select(t => (t.Name, t.Value.GetInt32())).ToArray();
        Assert.Equal(types, root.GetProperty("data").GetProperty("types").EnumerateArray().Select(t => (t.GetProperty("type").GetString()!, t.GetProperty("count").GetInt32())));
        Assert.Equal(types, result.Types.Select(t => (t.Type, t.Count)));

        Assert.Equal(c.GetProperty("deviceByDevice").GetBoolean(), result.DeviceByDevice);
        Assert.Equal(device, result.Device.DeviceId);
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task Device_sensors_and_the_host_operation_fail_alike_when_the_sensor_routes_fail(string name)
    {
        var failure = Transport.GetProperty("failures").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        var route = failure.GetProperty("route").GetString()!;
        var routes = Routes(route == "resources/sensors" ? "fleet" : "deviceByDevice");
        routes[route] = Text(failure, "behaviour") == "hang" ? Answer.Hang
            : new Answer(failure.GetProperty("status").GetInt32(),
                Transport.GetProperty("bodies").GetProperty(failure.GetProperty("body").GetString()!).GetString()!);
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var sensors = Host(host);

        // Side by side: a request that never gets an answer costs the full timeout on each.
        var mcpCall = Tools(mcp).DeviceSensors("1", cancellationToken: CancellationToken.None);
        var hostCall = sensors.ReadAsync(new(1));
        await Task.WhenAll(mcpCall, hostCall);
        var text = await mcpCall;
        var outcome = await hostCall;

        // MCP: an error in its own words, with no sensor data and nothing secret-shaped left.
        var root = FakeLibreNms.Parse(text);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        // Host: the shared category, no result and no server text.
        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<HostFailure>(failure.GetProperty("code").GetString()!), outcome.Error!.Code);
        Assert.DoesNotContain("synthetic-secret", outcome.Error.Message, StringComparison.Ordinal);

        Assert.Equal(Strings(failure, "requests"), mcpServer.Requests);
        Assert.Equal(Strings(failure, "requests"), hostServer.Requests);
    }
}
