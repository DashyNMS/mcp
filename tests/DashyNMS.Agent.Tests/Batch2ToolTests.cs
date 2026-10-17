using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>device_details (#23), device_sensors and health_overview (#24), device_neighbours (#32).</summary>
public sealed class Batch2ToolTests
{
    private readonly FakeLibreNms _nms = new();

    private HealthTools Health() => new(_nms.Sensors, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner);

    private DetailTools Details() => new(_nms.Client, _nms.Fleet, _nms.Sensors, _nms.Directory, _nms.Resolver, _nms.Runner);

    [Fact]
    public async Task Sensors_are_judged_like_the_health_tab()
    {
        var root = FakeLibreNms.Parse(await Health().DeviceSensors("core-sw1", cancellationToken: CancellationToken.None));
        var readings = root.GetProperty("data").GetProperty("readings").EnumerateArray().ToDictionary(r => r.GetProperty("sensor_id").GetInt32());

        Assert.Equal("7 sensors: 3 critical, 0 warning.", root.GetProperty("summary").GetString());
        Assert.Equal("ok", readings[1].GetProperty("state").GetString());
        Assert.Equal("°C", readings[1].GetProperty("unit").GetString());
        // Over the sensor's own limit.
        Assert.Equal("critical", readings[2].GetProperty("state").GetString());
        // Below the fan's own low limit.
        Assert.Equal("critical", readings[3].GetProperty("state").GetString());
        // No limits of its own: desktop's default dBm threshold applies.
        Assert.Equal("critical", readings[4].GetProperty("state").GetString());
        // The "nothing plugged in" sentinel isn't a fault.
        Assert.Equal("unknown", readings[5].GetProperty("state").GetString());
        // Problems come first.
        Assert.Equal("critical", root.GetProperty("data").GetProperty("readings")[0].GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("temp", 2)]
    [InlineData("fan", 1)]
    [InlineData("optics", 2)]
    [InlineData("humidity", 0)]
    public async Task Sensors_by_type_with_friendly_names(string type, int expected)
    {
        var root = FakeLibreNms.Parse(await Health().DeviceSensors("core-sw1", type, CancellationToken.None));

        Assert.Equal(expected, root.GetProperty("data").GetProperty("readings").GetArrayLength());
    }

    [Fact]
    public async Task Overview_lists_problems_across_the_network()
    {
        var root = FakeLibreNms.Parse(await Health().HealthOverview(cancellationToken: CancellationToken.None));
        var readings = root.GetProperty("data").GetProperty("readings");

        Assert.Equal("9 sensor readings checked: 3 critical, 0 warning.", root.GetProperty("summary").GetString());
        Assert.Equal(3, readings.GetArrayLength());
        Assert.All(readings.EnumerateArray(), r => Assert.Equal(1, r.GetProperty("device").GetProperty("device_id").GetInt32()));
    }

    [Fact]
    public async Task Overview_of_every_reading_of_a_type()
    {
        var root = FakeLibreNms.Parse(await Health().HealthOverview("temperature", only_problems: false, cancellationToken: CancellationToken.None));

        Assert.Equal("3 temperature readings checked: 1 critical, 0 warning, 2 other.", root.GetProperty("summary").GetString());
        Assert.Equal(3, root.GetProperty("data").GetProperty("readings").GetArrayLength());
    }

    [Fact]
    public async Task Overview_filters_by_group()
    {
        var root = FakeLibreNms.Parse(await Health().HealthOverview(group: "edge switches", cancellationToken: CancellationToken.None));

        Assert.Equal(0, root.GetProperty("data").GetProperty("readings").GetArrayLength());
    }

    [Fact]
    public async Task Every_reading_needs_a_type()
    {
        var root = FakeLibreNms.Parse(await Health().HealthOverview(only_problems: false, cancellationToken: CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Without_global_read_sensors_are_read_device_by_device()
    {
        _nms.Transport.Fails("resources/sensors", System.Net.HttpStatusCode.Forbidden);

        var root = FakeLibreNms.Parse(await Health().DeviceSensors("core-sw1", cancellationToken: CancellationToken.None));

        Assert.Equal("3 sensors: 2 critical, 0 warning.", root.GetProperty("summary").GetString());
        // Processor isn't a sensor: device_health covers it.
        Assert.DoesNotContain(_nms.Transport.Requests, r => r.StartsWith("devices/1/health/device_processor", StringComparison.Ordinal));

        // Refused once, the network-wide list isn't asked for again.
        await Health().DeviceSensors("core-sw1", cancellationToken: CancellationToken.None);
        Assert.Single(_nms.Transport.Requests, r => r == "resources/sensors");
    }

    [Fact]
    public async Task Without_global_read_the_overview_needs_narrowing()
    {
        _nms.Transport.Fails("resources/sensors", System.Net.HttpStatusCode.Forbidden);

        var everything = FakeLibreNms.Parse(await Health().HealthOverview(cancellationToken: CancellationToken.None));
        var core = FakeLibreNms.Parse(await Health().HealthOverview(group: "core switches", cancellationToken: CancellationToken.None));

        Assert.Equal("error", everything.GetProperty("status").GetString());
        Assert.Contains("global read access", everything.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("3 sensor readings checked: 2 critical, 0 warning.", core.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Details_default_sections_skip_the_big_tables()
    {
        var root = FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", cancellationToken: CancellationToken.None));
        var data = root.GetProperty("data");

        foreach (var section in DetailTools.DefaultSections)
        {
            Assert.True(data.TryGetProperty(section, out _), section);
        }

        Assert.False(data.TryGetProperty("fdb", out _));
        Assert.False(data.TryGetProperty("arp", out _));
        Assert.False(data.TryGetProperty("unavailable", out _));
    }

    [Fact]
    public async Task Inventory_leaves_out_ports()
    {
        var inventory = FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", ["inventory"], cancellationToken: CancellationToken.None))
            .GetProperty("data").GetProperty("inventory");

        Assert.Equal(2, inventory.GetArrayLength());
        Assert.Equal("SG00TEST01", inventory[0].GetProperty("serial").GetString());
        Assert.True(inventory[1].GetProperty("replaceable").GetBoolean());
    }

    [Fact]
    public async Task Vlans_list_untagged_and_tagged_ports()
    {
        var vlans = FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", ["vlans"], cancellationToken: CancellationToken.None))
            .GetProperty("data").GetProperty("vlans");

        var cameras = vlans[1];
        Assert.Equal(120, cameras.GetProperty("vlan").GetInt32());
        Assert.Equal(["1/1/7"], cameras.GetProperty("untagged_ports").EnumerateArray().Select(p => p.GetString()!).ToArray());
        Assert.Equal(["1/1/1"], cameras.GetProperty("tagged_ports").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Fact]
    public async Task Fdb_and_arp_name_the_port_and_vlan()
    {
        var data = FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", ["fdb", "arp"], cancellationToken: CancellationToken.None)).GetProperty("data");

        var mac = data.GetProperty("fdb")[0];
        Assert.Equal("1/1/7", mac.GetProperty("port").GetString());
        Assert.Equal("aa:bb:cc:dd:01:07", mac.GetProperty("mac").GetString());
        Assert.Equal(120, mac.GetProperty("vlan").GetInt32());
        Assert.Equal("10.0.0.3", data.GetProperty("arp")[0].GetProperty("ip").GetString());
        Assert.Equal("1/1/1", data.GetProperty("arp")[0].GetProperty("port").GetString());
    }

    [Fact]
    public async Task Poe_routing_availability_and_addresses()
    {
        var data = FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", ["poe", "routing", "availability", "addresses"], cancellationToken: CancellationToken.None)).GetProperty("data");

        var budget = data.GetProperty("poe").GetProperty("budgets")[0];
        Assert.Equal(370, budget.GetProperty("total_watts").GetDouble());
        Assert.Equal(120.5, budget.GetProperty("used_watts").GetDouble());

        var sessions = data.GetProperty("routing").GetProperty("bgp_sessions");
        Assert.Equal("idle", sessions[0].GetProperty("state").GetString());
        Assert.Equal("Hold Timer Expired", sessions[0].GetProperty("last_error").GetString());

        var availability = data.GetProperty("availability");
        Assert.Equal("day", availability[0].GetProperty("over").GetString());
        Assert.Equal("week", availability[1].GetProperty("over").GetString());
        Assert.Equal(99.941, availability[1].GetProperty("percent").GetDouble());

        var addresses = data.GetProperty("addresses");
        Assert.Equal("10.0.0.1/24", addresses[0].GetProperty("address").GetString());
        Assert.Equal("2001:db8::1/64", addresses[1].GetProperty("address").GetString());
        Assert.Equal("1/1/1", addresses[1].GetProperty("port").GetString());
    }

    [Theory]
    [InlineData("mac", "fdb")]
    [InlineData("bgp", "routing")]
    [InlineData("uptime", "availability")]
    public async Task Section_aliases(string alias, string section)
        => Assert.True(FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", [alias], cancellationToken: CancellationToken.None)).GetProperty("data").TryGetProperty(section, out _));

    [Fact]
    public async Task Unknown_section_is_an_error()
        => Assert.Equal("error", FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", ["firmware"], cancellationToken: CancellationToken.None)).GetProperty("status").GetString());

    [Fact]
    public async Task A_section_that_fails_is_listed_and_the_rest_stand()
    {
        _nms.Transport.Fails("inventory/1/all", System.Net.HttpStatusCode.Forbidden);

        var data = FakeLibreNms.Parse(await Details().DeviceDetails("core-sw1", ["inventory", "availability"], cancellationToken: CancellationToken.None)).GetProperty("data");

        Assert.Equal("inventory", data.GetProperty("unavailable")[0].GetProperty("source").GetString());
        Assert.True(data.TryGetProperty("availability", out _));
    }

    [Fact]
    public async Task Neighbours_include_unknown_devices()
    {
        var root = FakeLibreNms.Parse(await Details().DeviceNeighbours("core-sw1", CancellationToken.None));
        var neighbours = root.GetProperty("data").GetProperty("neighbours");

        Assert.Equal("2 neighbours (1 known to LibreNMS).", root.GetProperty("summary").GetString());
        Assert.Equal("1/1/1", neighbours[0].GetProperty("local_port").GetString());
        Assert.Equal(2, neighbours[0].GetProperty("remote_device_id").GetInt32());
        Assert.Equal("edge-sw3.example.com", neighbours[0].GetProperty("remote_hostname").GetString());
        Assert.Equal("cam-08", neighbours[1].GetProperty("remote_hostname").GetString());
        Assert.False(neighbours[1].TryGetProperty("remote_device_id", out _));
    }

    [Fact]
    public async Task Neighbours_reported_only_from_the_other_side()
    {
        var root = FakeLibreNms.Parse(await Details().DeviceNeighbours("edge-sw3", CancellationToken.None));
        var neighbour = root.GetProperty("data").GetProperty("neighbours")[0];

        Assert.Equal("1 neighbour (1 known to LibreNMS); 1 only reported from the other side.", root.GetProperty("summary").GetString());
        Assert.Equal("neighbour", neighbour.GetProperty("seen_from").GetString());
        Assert.Equal("49", neighbour.GetProperty("local_port").GetString());
        Assert.Equal("core-sw1.example.com", neighbour.GetProperty("remote_hostname").GetString());
    }
}
