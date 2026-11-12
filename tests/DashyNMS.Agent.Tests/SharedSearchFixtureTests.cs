using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The MCP find_device tool and HostDeviceSearch, the executor mobile's Intelligence
/// find_device wraps, against the device search fixture shared with DashyNMS/mobile
/// (Fixtures/DeviceSearch). Mobile keeps byte-identical copies and checks them against this
/// repository, so both adapters here and mobile's are held to the same ranking, totals,
/// request scope and status words from the same response.
/// </summary>
/// <remarks>
/// Failures are shared only as far as both adapters failing without data after the same
/// requests. The DeviceSearchFailure categories are the host's contract; the MCP tool returns
/// its own sentence for each HTTP status instead, asserted here per status. The README lists
/// what the fixture does not prove.
/// </remarks>
public sealed class SharedSearchFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceSearch");
    private static readonly string DevicesJson = File.ReadAllText(Path.Combine(FixtureDirectory, "devices.json"));

    private static JsonElement Expectations()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "cases.json")));
        return json.RootElement.Clone();
    }

    private static string[] ExpectedRequests() =>
        Expectations().GetProperty("requests").EnumerateArray().Select(r => r.GetString()!).ToArray();

    public static TheoryData<string, int[], int> Cases()
    {
        var data = new TheoryData<string, int[], int>();
        foreach (var c in Expectations().GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("query").GetString()!, c.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).ToArray(), c.GetProperty("total").GetInt32());
        }

        return data;
    }

    public static TheoryData<int, string> Failures()
    {
        var data = new TheoryData<int, string>();
        foreach (var f in Expectations().GetProperty("failures").EnumerateArray())
        {
            data.Add(f.GetProperty("status").GetInt32(), f.GetProperty("code").GetString()!);
        }

        return data;
    }

    private sealed record Wired(FakeTransport Transport, ILibreNmsClient Client, DeviceTools Tools);

    /// <summary>The tool as the server builds it, over Core's resource APIs and a transport holding only the fixture.</summary>
    private static Wired Wire(Action<FakeTransport> routes)
    {
        var transport = new FakeTransport();
        routes(transport);
        var apis = new LibreNmsResourceApis(transport);
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.Returns(apis.Devices);
        client.Arp.Returns(apis.Arp);
        client.DeviceGroups.Returns(apis.DeviceGroups);
        client.Locations.Returns(apis.Locations);
        var time = new FakeTimeProvider(FakeLibreNms.Now);
        var options = new AgentOptions();
        var directory = new DeviceDirectory(client, time);
        var tools = new DeviceTools(client, directory, new DeviceResolver(directory), new DeviceScope(client, directory, time),
            new ToolRunner(new Redactor(), options, NullLogger<ToolRunner>.Instance), options);
        return new(transport, client, tools);
    }

    /// <summary>The executor as a host gets it, over the same transport the fixture answers from.</summary>
    private static HostDeviceSearch Host(Wired wired) =>
        HostDeviceSearch.Create(wired.Client, new FakeTimeProvider(FakeLibreNms.Now), serverTimestampsAreUtc: false);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Find_device_ranks_and_counts_the_shared_fixture(string query, int[] ids, int total)
    {
        var wired = Wire(t => t.On("devices", DevicesJson));
        var text = await wired.Tools.FindDevice(query, limit: DeviceTools.MaxMatches, cancellationToken: CancellationToken.None);
        var root = FakeLibreNms.Parse(text);

        Assert.Equal("ok", root.GetProperty("status").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(ids, data.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("device_id").GetInt32()));
        Assert.Equal(total, data.GetProperty("page").GetProperty("total").GetInt32());
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        // Scope: a text search reads the device list and nothing else.
        Assert.Equal(ExpectedRequests(), wired.Transport.Requests);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Host_search_ranks_and_counts_the_shared_fixture(string query, int[] ids, int total)
    {
        var wired = Wire(t => t.On("devices", DevicesJson));
        using var search = Host(wired);
        var outcome = await search.FindDevicesAsync(new(query, Limit: DeviceSearch.MaxMatches));

        Assert.Null(outcome.Error);
        var result = outcome.Result!;
        Assert.False(result.IsMacSearch);
        Assert.Equal(ids, result.Matches.Select(m => m.Device.DeviceId));
        Assert.Equal(total, result.Page.Total);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(ExpectedRequests(), wired.Transport.Requests);
    }

    /// <summary>
    /// The status word is MCP's output and filter, and stays as it was; the reported state
    /// is the typed state hosts show, kept out of MCP's JSON in brief and verbose records.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Find_device_and_the_host_search_report_the_shared_status_words_and_reported_states(bool verbose)
    {
        var mcp = Wire(t => t.On("devices", DevicesJson));
        var devices = FakeLibreNms.Parse(await mcp.Tools.FindDevice("sw", limit: DeviceTools.MaxMatches, verbose: verbose, cancellationToken: CancellationToken.None))
            .GetProperty("data").GetProperty("devices").EnumerateArray().ToList();
        Assert.All(devices, d => Assert.False(d.TryGetProperty("state", out _)));
        var host = Wire(t => t.On("devices", DevicesJson));
        using var search = Host(host);
        var matches = (await search.FindDevicesAsync(new("sw", Limit: DeviceSearch.MaxMatches))).Result!.Matches;

        foreach (var state in Expectations().GetProperty("states").EnumerateArray())
        {
            var id = state.GetProperty("id").GetInt32();
            var shared = state.GetProperty("shared").GetString();
            Assert.Equal(shared, devices.Single(d => d.GetProperty("device_id").GetInt32() == id).GetProperty("status").GetString());
            var device = matches.Single(m => m.Device.DeviceId == id).Device;
            Assert.Equal(shared, device.Status);
            Assert.Equal(state.GetProperty("reported").GetString(), device.State.ToDisplayString());
        }
    }

    /// <summary>
    /// The MCP tool's own sentence for each failed status: not part of the shared fixture,
    /// since the categories are the host's contract and mobile words its failures itself.
    /// </summary>
    private static string McpMessageFor(int status) => status switch
    {
        401 => "LibreNMS: the API token was refused.",
        403 => $"LibreNMS: {LibreNmsApiException.PermissionDeniedMessage}",
        404 => "LibreNMS: it doesn't have what was asked for (HTTP 404).",
        >= 500 => $"LibreNMS: it had an error of its own (HTTP {status}). Try again shortly.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Add the MCP message for this fixture failure."),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Find_device_and_the_host_search_fail_without_data_after_the_shared_requests(int status, string code)
    {
        var mcp = Wire(t => t.Fails("devices", (HttpStatusCode)status));
        var root = FakeLibreNms.Parse(await mcp.Tools.FindDevice("sw", cancellationToken: CancellationToken.None));
        var host = Wire(t => t.Fails("devices", (HttpStatusCode)status));
        using var search = Host(host);
        var outcome = await search.FindDevicesAsync(new("sw"));

        // MCP: an error with its own sentence for this status, no data and no server text.
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(McpMessageFor(status), root.GetProperty("message").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.False(root.TryGetProperty("details", out _));
        // Host: the shared category, no result.
        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<DeviceSearchFailure>(code), outcome.Error!.Code);
        Assert.Equal(ExpectedRequests(), mcp.Transport.Requests);
        Assert.Equal(ExpectedRequests(), host.Transport.Requests);
    }
}
