using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

public sealed class DeviceToolsTests
{
    private readonly FakeLibreNms _nms = new();

    private DeviceTools Tools() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Runner, _nms.Options);

    [Fact]
    public async Task Find_ranks_exact_matches_first()
    {
        var root = FakeLibreNms.Parse(await Tools().FindDevice("sw", CancellationToken.None));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        var devices = root.GetProperty("data").GetProperty("devices");
        Assert.Equal(2, devices.GetArrayLength());

        var exact = FakeLibreNms.Parse(await Tools().FindDevice("edge-sw3", CancellationToken.None));
        var first = exact.GetProperty("data").GetProperty("devices")[0];
        Assert.Equal(2, first.GetProperty("device_id").GetInt32());
        Assert.Equal("down", first.GetProperty("status").GetString());
        // Display the same as the hostname adds nothing, so it's left out.
        Assert.False(first.TryGetProperty("display", out _));
    }

    [Fact]
    public async Task Find_with_no_match_is_still_ok()
    {
        var root = FakeLibreNms.Parse(await Tools().FindDevice("nothing-here", CancellationToken.None));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("No devices match.", root.GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aabb.ccdd.eeff")]
    public async Task Find_by_mac_uses_arp(string mac)
    {
        var root = FakeLibreNms.Parse(await Tools().FindDevice(mac, CancellationToken.None));

        var match = root.GetProperty("data").GetProperty("arp_matches")[0];
        Assert.Equal(1, match.GetProperty("device").GetProperty("device_id").GetInt32());
        Assert.Equal("10.0.10.50", match.GetProperty("seen_as")[0].GetProperty("ip").GetString());
        Assert.Contains("resources/ip/arp/aa:bb:cc:dd:ee:ff", _nms.Transport.Requests);
    }

    [Fact]
    public async Task Health_combines_state_alerts_sensors_and_ports()
    {
        var root = FakeLibreNms.Parse(await Tools().DeviceHealth("core-sw1", CancellationToken.None));
        var data = root.GetProperty("data");

        Assert.Equal("Device is up, 2 open alerts, 1 port down while enabled; 1 part couldn't be fetched.", root.GetProperty("summary").GetString());
        Assert.Equal(864100, data.GetProperty("uptime_seconds").GetInt64());
        Assert.False(data.GetProperty("under_maintenance").GetBoolean());
        Assert.Equal(61, data.GetProperty("cpu_max_percent").GetDouble());
        Assert.Equal(48.3, data.GetProperty("memory_max_percent").GetDouble());

        var alerts = data.GetProperty("open_alerts");
        Assert.Equal(2, alerts.GetArrayLength());
        Assert.Equal(10, alerts[0].GetProperty("alert_id").GetInt32());
        Assert.Equal("critical", alerts[0].GetProperty("severity").GetString());
        Assert.Equal("2026-10-17T09:40:00Z", alerts[0].GetProperty("since").GetString());

        // Errors first, then down ports; the admin-down spare isn't a problem.
        var problems = data.GetProperty("problem_ports");
        Assert.Equal(2, problems.GetArrayLength());
        Assert.Equal("1/1/8", problems[0].GetProperty("if_name").GetString());
        Assert.Equal("1/1/7", problems[1].GetProperty("if_name").GetString());
        Assert.Equal(4, data.GetProperty("ports_total").GetInt32());
    }

    [Fact]
    public async Task Health_reports_the_part_it_could_not_fetch()
    {
        var data = FakeLibreNms.Parse(await Tools().DeviceHealth("1", CancellationToken.None)).GetProperty("data");

        var unavailable = data.GetProperty("unavailable");
        Assert.Equal("storage", unavailable[0].GetProperty("source").GetString());
        Assert.StartsWith("LibreNMS: Your API token isn't allowed", unavailable[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(data.TryGetProperty("fullest_storage", out _));
    }

    [Fact]
    public async Task Health_for_an_unknown_device_is_an_error()
    {
        var root = FakeLibreNms.Parse(await Tools().DeviceHealth("nope", CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }
}
