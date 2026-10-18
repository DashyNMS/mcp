using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Tools;
using ModelContextProtocol.Protocol;

namespace DashyNMS.Agent.Tests;

/// <summary>graph (#33) and trace_endpoint (#19).</summary>
public sealed class Batch6ToolTests
{
    /// <summary>A PNG's 8-byte signature: enough for the tool's check, and nothing else to decode.</summary>
    private const string Png = """{"status":"ok","image":{"image":"iVBORw0KGgo=","content-type":"image/png"}}""";

    private const string EdgeSwitchPorts = """
        {"status":"ok","ports":[
          {"port_id":249,"device_id":2,"ifName":"49","ifAlias":"Uplink to core","ifAdminStatus":"up","ifOperStatus":"up"},
          {"port_id":205,"device_id":2,"ifName":"5","ifAlias":"Desk 5","ifAdminStatus":"up","ifOperStatus":"up","ifSpeed":1000000000},
          {"port_id":260,"device_id":2,"ifName":"Trk1","ifType":"ieee8023adLag","ifAdminStatus":"up","ifOperStatus":"up"}
        ]}
        """;

    private static GraphTools Graphs(FakeLibreNms nms) => new(nms.Client, nms.Transport, nms.Sensors, nms.Resolver, nms.Runner, nms.Time);

    private static TraceTools Trace(FakeLibreNms nms) => new(nms.Client, nms.Transport, nms.Fleet, nms.Directory, nms.Runner, nms.Options);

    private static FakeLibreNms WithGraphs()
    {
        var nms = new FakeLibreNms();
        nms.Transport
            .On("devices/1/graphs", """{"status":"ok","graphs":[{"desc":"Traffic","name":"device_bits"},{"desc":"Ping Response","name":"device_icmp_perf"}]}""")
            .On("devices/1/device_bits", Png)
            .On("devices/1/device_processor", Png)
            .On("devices/1/ports/1%2F1%2F7/port_errors", Png);
        return nms;
    }

    private static JsonElement Text(CallToolResult result)
        => FakeLibreNms.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);

    [Fact]
    public async Task Device_graph_defaults_to_traffic_as_a_png_and_lists_the_types()
    {
        var nms = WithGraphs();

        var result = await Graphs(nms).Graph("core-sw1");

        var root = Text(result);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("Traffic graph for the device over the last day, shown as an image.", root.GetProperty("summary").GetString());
        Assert.Equal("device_bits", root.GetProperty("data").GetProperty("graph").GetProperty("type").GetString());

        // All three listings, the wireless one failing without costing the rest.
        var types = root.GetProperty("data").GetProperty("available_types").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("device_icmp_perf", types);
        Assert.Contains("device_temperature", types);

        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(0x89, image.DecodedData.Span[0]);

        var request = Assert.Single(nms.Transport.Requests, r => r.StartsWith("devices/1/device_bits?", StringComparison.Ordinal));
        Assert.Contains("from=-1day", request, StringComparison.Ordinal);
        Assert.EndsWith("&graph_type=png&output=base64&bg=FFFFFF&bbg=FFFFFF", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everyday_words_pick_a_health_graph_without_a_legend()
    {
        var nms = WithGraphs();

        var result = await Graphs(nms).Graph("1", type: "cpu", range: "week", legend: false);

        Assert.Equal("device_processor", Text(result).GetProperty("data").GetProperty("graph").GetProperty("type").GetString());
        var request = Assert.Single(nms.Transport.Requests, r => r.StartsWith("devices/1/device_processor?", StringComparison.Ordinal));
        Assert.Contains("from=-1week", request, StringComparison.Ordinal);
        Assert.Contains("&legend=no", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_type_is_an_error_listing_the_real_ones_and_no_image()
    {
        var result = await Graphs(WithGraphs()).Graph("core-sw1", type: "device_toasters");

        var root = Text(result);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Contains(root.GetProperty("details").GetProperty("available_types").EnumerateArray(), t => t.GetProperty("name").GetString() == "device_bits");
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task Device_without_traffic_gives_its_list_instead()
    {
        var nms = WithGraphs();
        nms.Transport.On("devices/1/graphs", """{"status":"ok","graphs":[{"desc":"Ping Response","name":"device_icmp_perf"}]}""");

        var result = await Graphs(nms).Graph("core-sw1");

        Assert.StartsWith("This device has no traffic graph", Text(result).GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task Port_graph_goes_by_escaped_name_over_a_custom_span()
    {
        var nms = WithGraphs();

        var result = await Graphs(nms).Graph("core-sw1", type: "errors", port: "Camera 7", since: "6h");

        var data = Text(result).GetProperty("data");
        Assert.Equal("port_errors", data.GetProperty("graph").GetProperty("type").GetString());
        Assert.Equal(107, data.GetProperty("port").GetProperty("port_id").GetInt32());
        Assert.IsType<ImageContentBlock>(result.Content[1]);

        var from = FakeLibreNms.Now.AddHours(-6).ToUnixTimeSeconds();
        var request = Assert.Single(nms.Transport.Requests, r => r.StartsWith("devices/1/ports/1%2F1%2F7/port_errors?", StringComparison.Ordinal));
        Assert.Contains($"from={from}&", request, StringComparison.Ordinal);
        Assert.Contains($"&to={FakeLibreNms.Now.ToUnixTimeSeconds()}", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Svg_from_an_older_librenms_is_an_error_not_a_broken_image()
    {
        var nms = WithGraphs();
        nms.Transport.On("devices/1/device_bits", """{"status":"ok","image":{"image":"PHN2Zz4=","content-type":"image/svg+xml"}}""");

        var result = await Graphs(nms).Graph("core-sw1");

        Assert.Contains("image/svg+xml", Text(result).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(result.Content);
    }

    [Theory]
    [InlineData("fortnight", null, null)]
    [InlineData(null, null, "2h")]
    [InlineData(null, "40d", null)]
    public async Task Bad_spans_are_errors(string? range, string? since, string? until)
    {
        var root = Text(await Graphs(WithGraphs()).Graph("core-sw1", range: range, since: since, until: until));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Module 2", null, "device_temperature/2")]
    [InlineData("4", null, "device_dbm/4")]
    [InlineData("1/1/50", null, "device_dbm/5")]
    // With a type, only that kind counts: "2" isn't fan sensor 2, so it's the fan called "Fan 2".
    [InlineData("2", "fanspeed", "device_fanspeed/3")]
    [InlineData("chassis", "device_temperature", "device_temperature/1")]
    public async Task One_sensor_is_drawn_on_its_own(string sensor, string? type, string route)
    {
        var nms = WithGraphs();
        nms.Transport.On("devices/1/graphs/health/" + route, Png);

        var result = await Graphs(nms).Graph("core-sw1", type: type, sensor: sensor);

        var root = Text(result);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(int.Parse(route.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture), root.GetProperty("data").GetProperty("sensor").GetProperty("sensor_id").GetInt32());
        Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Contains(nms.Transport.Requests, r => r.StartsWith("devices/1/graphs/health/" + route + "?", StringComparison.Ordinal) && r.Contains("bg=FFFFFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_matching_sensors_are_listed_to_choose_from()
    {
        var root = Text(await Graphs(WithGraphs()).Graph("core-sw1", sensor: "Rx Power"));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal([4, 5], root.GetProperty("details").GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("sensor_id").GetInt32()).ToArray());
    }

    [Theory]
    [InlineData("Toaster", null)]
    [InlineData("Chassis", "voltage")]
    public async Task No_matching_sensor_is_an_error(string sensor, string? type)
    {
        var result = await Graphs(WithGraphs()).Graph("core-sw1", type: type, sensor: sensor);

        Assert.Equal("error", Text(result).GetProperty("status").GetString());
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task Port_and_sensor_together_is_an_error()
    {
        var root = Text(await Graphs(WithGraphs()).Graph("core-sw1", port: "1/1/7", sensor: "Chassis"));

        Assert.Equal("Give a port or a sensor, not both.", root.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null, "port_bits")]
    [InlineData("traffic", "port_bits")]
    [InlineData("port_upkts", "port_upkts")]
    [InlineData("ERRORS", "port_errors")]
    public void Port_types(string? text, string expected) => Assert.Equal(expected, GraphTools.PortType(text).Type);

    [Theory]
    [InlineData("aa:bb:cc:00:00:05", "aa:bb:cc:00:00:05")]
    [InlineData("AA-BB-CC-00-00-05", "aa:bb:cc:00:00:05")]
    [InlineData("aabb.cc00.0005", "aa:bb:cc:00:00:05")]
    [InlineData("aabbcc000005", "aa:bb:cc:00:00:05")]
    [InlineData("10.0.0.5", null)]
    [InlineData("aabbcc00000", null)]
    [InlineData("core-sw1", null)]
    public void Mac_forms(string text, string? expected) => Assert.Equal(expected, TraceTools.Mac(text));

    /// <summary>
    /// The MAC on the core's uplink to edge-sw3 and on edge-sw3's port 5, plus
    /// whatever else a test adds: the textbook case is the edge port.
    /// </summary>
    private static FakeLibreNms WithMac(string fdbRows, string edgeFdb = """{"status":"ok","ports_fdb":[{"port_id":205,"device_id":2,"mac_address":"aabbcc000005"}]}""")
    {
        var nms = new FakeLibreNms();
        nms.Transport
            .On("resources/fdb/aabbcc000005", fdbRows)
            .On("devices/2/ports", EdgeSwitchPorts)
            .On("devices/2/fdb", edgeFdb)
            .On("resources/ip/arp/aa:bb:cc:00:00:05", """{"status":"ok","arp":[{"id":9,"port_id":101,"device_id":1,"mac_address":"aabbcc000005","ipv4_address":"10.0.0.50"}]}""");
        return nms;
    }

    private const string CoreAndEdge = """
        {"status":"ok","ports_fdb":[
          {"ports_fdb_id":1,"port_id":101,"device_id":1,"mac_address":"aabbcc000005","vlan_id":10,"updated_at":"2026-10-17 09:50:00"},
          {"ports_fdb_id":2,"port_id":205,"device_id":2,"mac_address":"aabbcc000005","vlan_id":20,"updated_at":"2026-10-17 09:50:00"}
        ]}
        """;

    [Fact]
    public async Task Mac_is_traced_past_the_uplink_to_the_edge_port()
    {
        var nms = WithMac(CoreAndEdge);

        var root = FakeLibreNms.Parse(await Trace(nms).TraceEndpoint("AA-BB-CC-00-00-05"));
        var data = root.GetProperty("data");

        Assert.Equal("Most likely on edge-sw3.example.com, port 5.", root.GetProperty("summary").GetString());
        Assert.False(data.GetProperty("ambiguous").GetBoolean());
        Assert.Equal("aa:bb:cc:00:00:05", data.GetProperty("mac").GetString());
        Assert.Equal("10.0.0.50", data.GetProperty("ips")[0].GetString());

        var edge = data.GetProperty("edge");
        Assert.Equal("Desk 5", edge.GetProperty("alias").GetString());
        Assert.Equal(1, edge.GetProperty("vlans")[0].GetInt32());
        Assert.Equal(1, edge.GetProperty("macs_on_port").GetInt32());

        // From the edge upwards: the core learned it on its port toward edge-sw3.
        Assert.Equal(["edge-sw3.example.com", "core-sw1.example.com"], data.GetProperty("path").EnumerateArray().Select(p => p.GetString()!).ToArray());
        var uplink = data.GetProperty("seen_on").EnumerateArray().Single(s => s.GetProperty("role").GetString() == "uplink");
        Assert.Equal("1/1/1", uplink.GetProperty("port").GetString());
        Assert.Contains("resources/fdb/aabbcc000005", nms.Transport.Requests);
    }

    [Fact]
    public async Task Ip_is_turned_into_its_mac_first()
    {
        var nms = WithMac(CoreAndEdge);
        nms.Transport.On("resources/ip/arp/10.0.0.50", """{"status":"ok","arp":[{"id":9,"port_id":101,"device_id":1,"mac_address":"aabbcc000005","ipv4_address":"10.0.0.50"}]}""");

        var data = FakeLibreNms.Parse(await Trace(nms).TraceEndpoint("10.0.0.50")).GetProperty("data");

        Assert.Equal("aa:bb:cc:00:00:05", data.GetProperty("mac").GetString());
        Assert.Equal(205, data.GetProperty("edge").GetProperty("port_id").GetInt32());
    }

    [Fact]
    public async Task Two_access_ports_make_it_ambiguous_quietest_first()
    {
        // Also on the core's Camera 7 port, which holds two MACs to port 5's one.
        var nms = WithMac("""
            {"status":"ok","ports_fdb":[
              {"port_id":107,"device_id":1,"mac_address":"aabbcc000005","vlan_id":11,"updated_at":"2026-10-17 09:55:00"},
              {"port_id":205,"device_id":2,"mac_address":"aabbcc000005","vlan_id":20,"updated_at":"2026-10-17 09:50:00"}
            ]}
            """);
        nms.Transport.On("devices/1/fdb", """{"status":"ok","ports_fdb":[{"port_id":107,"device_id":1,"mac_address":"aabbcc000005"},{"port_id":107,"device_id":1,"mac_address":"aabbcc000099"}]}""");

        var root = FakeLibreNms.Parse(await Trace(nms).TraceEndpoint("aabbcc000005"));
        var data = root.GetProperty("data");

        Assert.True(data.GetProperty("ambiguous").GetBoolean());
        Assert.Equal("Most likely on edge-sw3.example.com, port 5, but 1 other port is possible too.", root.GetProperty("summary").GetString());
        Assert.Equal(107, data.GetProperty("other_candidates")[0].GetProperty("port_id").GetInt32());
    }

    [Fact]
    public async Task Busy_ports_and_aggregates_are_passed_over_but_a_busy_port_is_a_last_guess()
    {
        // On edge-sw3's aggregate and on a port holding a MAC per VLAN, like a firewall's.
        var busy = string.Join(',', Enumerable.Range(0, 12).Select(i => $$"""{"port_id":205,"device_id":2,"mac_address":"aabbcc0000{{i:x2}}"}"""));
        var nms = WithMac("""
            {"status":"ok","ports_fdb":[
              {"port_id":260,"device_id":2,"mac_address":"aabbcc000005","vlan_id":20},
              {"port_id":205,"device_id":2,"mac_address":"aabbcc000005","vlan_id":20}
            ]}
            """, $$"""{"status":"ok","ports_fdb":[{{busy}}]}""");

        var root = FakeLibreNms.Parse(await Trace(nms).TraceEndpoint("aabbcc000005"));
        var data = root.GetProperty("data");

        Assert.StartsWith("Probably on edge-sw3.example.com, port 5, though that port holds many MACs", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(data.GetProperty("ambiguous").GetBoolean());
        Assert.Equal("busy port", data.GetProperty("edge").GetProperty("role").GetString());
        Assert.Contains(data.GetProperty("seen_on").EnumerateArray(), s => s.GetProperty("role").GetString() == "aggregate");
    }

    [Fact]
    public async Task Only_uplinks_means_no_clear_edge_port()
    {
        var nms = WithMac("""{"status":"ok","ports_fdb":[{"port_id":101,"device_id":1,"mac_address":"aabbcc000005","vlan_id":10}]}""");

        var root = FakeLibreNms.Parse(await Trace(nms).TraceEndpoint("aabbcc000005"));

        Assert.Equal("Seen on 1 switch, but only on links between switches: no clear edge port.", root.GetProperty("summary").GetString());
        Assert.False(root.GetProperty("data").TryGetProperty("edge", out _));
    }

    [Fact]
    public async Task Mac_in_no_fdb_is_not_found_rather_than_an_error()
    {
        // No fake for resources/fdb/..., so LibreNMS's 404 "Fdb entry does not exist".
        var root = FakeLibreNms.Parse(await Trace(new FakeLibreNms()).TraceEndpoint("aa:bb:cc:dd:ee:01"));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("data").GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Ip_in_no_arp_table_is_not_found()
    {
        var root = FakeLibreNms.Parse(await Trace(new FakeLibreNms()).TraceEndpoint("10.0.0.77"));

        Assert.False(root.GetProperty("data").GetProperty("found").GetBoolean());
        Assert.StartsWith("No device's ARP table has this IP", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_ip_with_two_macs_traces_both()
    {
        var nms = WithMac(CoreAndEdge);
        nms.Transport.On("resources/ip/arp/10.0.0.50", """
            {"status":"ok","arp":[
              {"id":9,"port_id":101,"device_id":1,"mac_address":"aabbcc000005","ipv4_address":"10.0.0.50"},
              {"id":10,"port_id":101,"device_id":1,"mac_address":"aabbcc000006","ipv4_address":"10.0.0.50"}
            ]}
            """);

        var root = FakeLibreNms.Parse(await Trace(nms).TraceEndpoint("10.0.0.50"));
        var traces = root.GetProperty("data").GetProperty("traces");

        Assert.Equal(2, traces.GetArrayLength());
        Assert.True(traces[0].GetProperty("found").GetBoolean());
        Assert.False(traces[1].GetProperty("found").GetBoolean());
        Assert.True(root.GetProperty("data").GetProperty("ambiguous").GetBoolean());
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("core-sw1")]
    [InlineData("")]
    public async Task Other_input_is_an_error(string address)
    {
        var root = FakeLibreNms.Parse(await Trace(new FakeLibreNms()).TraceEndpoint(address));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }
}
