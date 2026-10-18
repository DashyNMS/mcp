using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>network_overview (#25), the top_ tools (#26), network_events (#29), alert_details (#27) and alert_rules (#28).</summary>
public sealed class Batch3ToolTests
{
    private readonly FakeLibreNms _nms = new();

    private NetworkTools Network() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private RuleTools Rules() => new(_nms.Client, _nms.Transport, _nms.Fleet, _nms.Directory, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private static int[] DeviceIds(System.Text.Json.JsonElement list)
        => list.EnumerateArray().Select(x => x.GetProperty("device").GetProperty("device_id").GetInt32()).ToArray();

    [Fact]
    public async Task Acknowledged_alert_is_found_although_alerts_by_id_only_gives_active_ones()
    {
        // As LibreNMS answers: nothing for the plain lookup, the alert when asked for state 2.
        _nms.Transport
            .On("alerts/12", """{"status":"ok","alerts":[],"count":0}""")
            .On("alerts/12?state=2", """
                {"status":"ok","alerts":[{"id":12,"device_id":1,"rule_id":3,"state":2,"timestamp":"2026-10-17 09:40:00","hostname":"core-sw1.example.com","severity":"critical","name":"Port down","note":"Ack (ops) cable ordered"}]}
                """);

        var root = FakeLibreNms.Parse(await Rules().AlertDetails(12, CancellationToken.None));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(12, root.GetProperty("data").GetProperty("alert").GetProperty("alert_id").GetInt32());
        Assert.Contains("acknowledged", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alert_in_no_state_is_still_an_error()
    {
        _nms.Transport.On("alerts/13", """{"status":"ok","alerts":[],"count":0}""");

        var root = FakeLibreNms.Parse(await Rules().AlertDetails(13, CancellationToken.None));

        Assert.StartsWith("No alert has that ID", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_gives_the_dashboard_counts()
    {
        var root = FakeLibreNms.Parse(await Network().NetworkOverview(CancellationToken.None));
        var data = root.GetProperty("data");

        Assert.Equal("2 devices: 1 up, 1 down; 3 open alerts (2 critical, 1 warning).", root.GetProperty("summary").GetString());
        Assert.Equal(1, data.GetProperty("alerts").GetProperty("acknowledged").GetInt32());
        Assert.Equal(2, data.GetProperty("alerts").GetProperty("devices_alerting").GetInt32());
        Assert.Equal(4, data.GetProperty("ports").GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("ports").GetProperty("down_while_enabled").GetInt32());
        Assert.Equal(2, data.GetProperty("ports").GetProperty("counting_errors").GetInt32());
        Assert.Equal("Pit lane", data.GetProperty("most_down_by_location")[0].GetProperty("location").GetString());
        Assert.Equal("26.10.0", data.GetProperty("librenms_version").GetString());
    }

    [Fact]
    public async Task Top_interfaces_by_total_with_utilisation()
    {
        var root = FakeLibreNms.Parse(await Network().TopInterfaces(cancellationToken: CancellationToken.None));
        var ports = root.GetProperty("data").GetProperty("ports");

        Assert.Equal([101, 249, 108], ports.EnumerateArray().Select(p => p.GetProperty("port_id").GetInt32()).ToArray());
        Assert.Equal(1000, ports[0].GetProperty("in_mbps").GetDouble());
        Assert.Equal(200, ports[0].GetProperty("out_mbps").GetDouble());
        Assert.Equal(10, ports[0].GetProperty("busiest_direction_percent").GetDouble());
        Assert.Equal("core-sw1.example.com", ports[0].GetProperty("device").GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task Top_interfaces_by_direction()
    {
        var ports = FakeLibreNms.Parse(await Network().TopInterfaces("out", 1, cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("ports");

        Assert.Equal(249, ports[0].GetProperty("port_id").GetInt32());
    }

    [Fact]
    public async Task Top_errors_and_their_group_filter()
    {
        var all = FakeLibreNms.Parse(await Network().TopErrors(cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("ports");
        var core = FakeLibreNms.Parse(await Network().TopErrors(group: "core switches", cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("ports");

        Assert.Equal([108, 249], all.EnumerateArray().Select(p => p.GetProperty("port_id").GetInt32()).ToArray());
        Assert.Equal(2.5, all[0].GetProperty("in_errors_per_sec").GetDouble());
        Assert.Equal(1, core.GetArrayLength());
    }

    [Theory]
    [InlineData(null, new[] { 1, 2 })]
    [InlineData("problems", new[] { 1, 2 })]
    [InlineData("out", new[] { 2, 1 })]
    public async Task Top_devices(string? by, int[] expected)
    {
        var devices = FakeLibreNms.Parse(await Network().TopDevices(by, cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("devices");

        Assert.Equal(expected, DeviceIds(devices));
    }

    [Fact]
    public async Task Top_devices_by_problems_counts_them()
    {
        var first = FakeLibreNms.Parse(await Network().TopDevices("problems", cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("devices")[0];

        Assert.Equal(1, first.GetProperty("ports_down_while_enabled").GetInt32());
        Assert.Equal(1, first.GetProperty("ports_counting_errors").GetInt32());
    }

    [Fact]
    public async Task Bad_rank_is_an_error()
        => Assert.Equal("error", FakeLibreNms.Parse(await Network().TopInterfaces("sideways", cancellationToken: CancellationToken.None)).GetProperty("status").GetString());

    [Fact]
    public async Task Network_events_for_the_last_day()
    {
        var root = FakeLibreNms.Parse(await Network().NetworkEvents(cancellationToken: CancellationToken.None));
        var entries = root.GetProperty("data").GetProperty("entries");

        Assert.Equal("In the last 24 hours: 3 events on 2 devices.", root.GetProperty("summary").GetString());
        Assert.Equal("Device status changed to Down", entries[0].GetProperty("text").GetString());
        Assert.Equal("critical", entries[0].GetProperty("severity").GetString());
        Assert.Equal("edge-sw3.example.com", entries[0].GetProperty("hostname").GetString());
    }

    [Theory]
    [InlineData("reboot", null, null, 1)]
    [InlineData(null, "warning", null, 2)]
    [InlineData(null, null, "edge switches", 2)]
    public async Task Network_events_filters(string? type, string? severity, string? group, int expected)
    {
        var root = FakeLibreNms.Parse(await Network().NetworkEvents(type: type, severity: severity, group: group, cancellationToken: CancellationToken.None));

        Assert.Equal(expected, root.GetProperty("data").GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Network_events_from_both_logs()
    {
        var root = FakeLibreNms.Parse(await Network().NetworkEvents("both", cancellationToken: CancellationToken.None));

        Assert.Equal("In the last 24 hours: 3 events and 3 alert changes on 2 devices.", root.GetProperty("summary").GetString());
        var alert = root.GetProperty("data").GetProperty("entries").EnumerateArray().First(e => e.GetProperty("source").GetString() == "alertlog");
        Assert.Equal("Port down", alert.GetProperty("rule").GetString());
    }

    [Fact]
    public async Task Bad_log_name_is_an_error()
        => Assert.Equal("error", FakeLibreNms.Parse(await Network().NetworkEvents("syslog", cancellationToken: CancellationToken.None)).GetProperty("status").GetString());

    [Fact]
    public async Task Alert_details_explain_why_it_fired()
    {
        var root = FakeLibreNms.Parse(await Rules().AlertDetails(10, CancellationToken.None));
        var data = root.GetProperty("data");

        Assert.Equal("A critical active alert, matching 1 item; it fired once before in the last 30 days.", root.GetProperty("summary").GetString());
        Assert.Equal(20, data.GetProperty("alert").GetProperty("open_minutes").GetInt64());

        var rule = data.GetProperty("rule");
        Assert.Equal("Port down", rule.GetProperty("name").GetString());
        Assert.Contains("ifOperStatus", rule.GetProperty("condition").GetString(), StringComparison.Ordinal);
        Assert.Equal("https://wiki.example.com/port-down", rule.GetProperty("procedure").GetString());

        var fault = data.GetProperty("why").GetProperty("faults")[0];
        Assert.Contains("1/1/7", fault.GetProperty("what").GetString(), StringComparison.Ordinal);
        Assert.True(data.GetProperty("why").GetProperty("rule_condition_known").GetBoolean());
        Assert.Contains(fault.GetProperty("trigger_values").EnumerateArray(), v => v.GetProperty("value").GetString() == "down");

        Assert.Equal("2026-10-08T09:00:00Z", data.GetProperty("history").GetProperty("recent")[0].GetString());
    }

    [Fact]
    public async Task Unknown_alert_is_an_error()
    {
        var root = FakeLibreNms.Parse(await Rules().AlertDetails(999, CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Alert_rules_with_coverage_template_and_counts()
    {
        var root = FakeLibreNms.Parse(await Rules().AlertRules(cancellationToken: CancellationToken.None));
        var rules = root.GetProperty("data").GetProperty("rules");

        Assert.Equal("3 rules enabled.", root.GetProperty("summary").GetString());
        Assert.Equal(["Device down", "High CPU", "Port down"], rules.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray());

        var port = rules[2];
        Assert.Equal("Core switches", port.GetProperty("applies_to").GetProperty("groups")[0].GetString());
        Assert.Equal("Port template", port.GetProperty("template").GetString());
        Assert.Equal(1, port.GetProperty("open_alerts").GetInt32());
        Assert.Equal(1, port.GetProperty("raised_last_7_days").GetInt32());

        Assert.Equal("edge-sw3.example.com", rules[0].GetProperty("applies_to").GetProperty("devices")[0].GetString());
        Assert.Equal("Paddock", rules[1].GetProperty("applies_to").GetProperty("locations")[0].GetString());
        Assert.Equal("default", rules[1].GetProperty("template").GetString());
        Assert.False(port.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task Alert_rules_filters_sorting_and_verbose()
    {
        var disabled = FakeLibreNms.Parse(await Rules().AlertRules(include_disabled: true, cancellationToken: CancellationToken.None));
        var critical = FakeLibreNms.Parse(await Rules().AlertRules(severity: "critical", cancellationToken: CancellationToken.None));
        var noisy = FakeLibreNms.Parse(await Rules().AlertRules(sort: "raised", cancellationToken: CancellationToken.None));
        var verbose = FakeLibreNms.Parse(await Rules().AlertRules(name: "port", verbose: true, cancellationToken: CancellationToken.None));

        Assert.Equal(4, disabled.GetProperty("data").GetProperty("rules").GetArrayLength());
        Assert.Equal(1, critical.GetProperty("data").GetProperty("rules").GetArrayLength());
        Assert.Equal("High CPU", noisy.GetProperty("data").GetProperty("rules")[2].GetProperty("name").GetString());
        Assert.Equal("Check the patching first.", verbose.GetProperty("data").GetProperty("rules")[0].GetProperty("notes").GetString());
    }
}
