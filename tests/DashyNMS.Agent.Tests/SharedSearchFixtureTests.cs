using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The MCP find_device tool against the device search fixture DashyNMS/mobile runs its
/// Intelligence find_device against (Fixtures/DeviceSearch). Mobile keeps byte-identical
/// copies and checks them against this repository, so both adapters are held to the same
/// ranking, totals, scope, status words and failure categories from the same response.
/// </summary>
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

    [Fact]
    public async Task Find_device_reports_the_shared_status_words()
    {
        var wired = Wire(t => t.On("devices", DevicesJson));
        var devices = FakeLibreNms.Parse(await wired.Tools.FindDevice("sw", limit: DeviceTools.MaxMatches, cancellationToken: CancellationToken.None))
            .GetProperty("data").GetProperty("devices").EnumerateArray().ToList();

        foreach (var state in Expectations().GetProperty("states").EnumerateArray())
        {
            var id = state.GetProperty("id").GetInt32();
            var device = devices.Single(d => d.GetProperty("device_id").GetInt32() == id);
            Assert.Equal(state.GetProperty("shared").GetString(), device.GetProperty("status").GetString());
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Find_device_and_the_host_search_fail_without_data_in_the_shared_categories(int status, string code)
    {
        var mcp = Wire(t => t.Fails("devices", (HttpStatusCode)status));
        var root = FakeLibreNms.Parse(await mcp.Tools.FindDevice("sw", cancellationToken: CancellationToken.None));
        var host = Wire(t => t.Fails("devices", (HttpStatusCode)status));
        using var search = HostDeviceSearch.Create(host.Client, new FakeTimeProvider(FakeLibreNms.Now), serverTimestampsAreUtc: false);
        var outcome = await search.FindDevicesAsync(new("sw"));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<DeviceSearchFailure>(code), outcome.Error!.Code);
        Assert.Equal(ExpectedRequests(), mcp.Transport.Requests);
        Assert.Equal(ExpectedRequests(), host.Transport.Requests);
    }
}
