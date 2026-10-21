using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>Every tool counts LibreNMS 26.10's alerts the same way (#77).</summary>
public sealed class AlertCountingToolsTests
{
    private readonly FakeLibreNms _nms = new();

    public AlertCountingToolsTests()
    {
        // Eight cores of core-sw1 over the CPU rule (two acknowledged), and edge-sw3 down.
        var cores = Enumerable.Range(0, 8).Select(i => Fault(9200 + i, device: 1, rule: 5, i is 3 or 5 ? 2 : 1, 300 + i, "warning", "High CPU"));
        var down = Fault(9300, device: 2, rule: 4, 1, null, "critical", "Device down");
        _nms.Transport.On("faults", $$"""{"status":"ok","alerts":[{{string.Join(",", cores.Append(down))}}]}""");
    }

    private AlertTools Alerts() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private NetworkTools Network() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private DeviceTools Devices() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options);

    private RuleTools Rules() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task The_same_alerts_give_the_same_totals_from_every_tool()
    {
        var active = FakeLibreNms.Parse(await Alerts().ActiveAlerts(cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("totals");
        var overview = FakeLibreNms.Parse(await Network().NetworkOverview(CancellationToken.None)).GetProperty("data").GetProperty("alerts");

        Assert.Equal(active.GetProperty("alert_groups").GetInt32(), overview.GetProperty("open").GetInt32());
        Assert.Equal(active.GetProperty("faults").GetInt32(), overview.GetProperty("faults").GetInt32());
        Assert.Equal(active.GetProperty("critical").GetInt32(), overview.GetProperty("critical").GetInt32());
        Assert.Equal(active.GetProperty("warning").GetInt32(), overview.GetProperty("warning").GetInt32());
        Assert.Equal(active.GetProperty("partly_acknowledged").GetInt32(), overview.GetProperty("partly_acknowledged").GetInt32());
        Assert.Equal(2, overview.GetProperty("open").GetInt32());
        Assert.Equal(9, overview.GetProperty("faults").GetInt32());
    }

    [Fact]
    public async Task A_device_counts_one_alert_of_eight_faults()
    {
        var root = FakeLibreNms.Parse(await Devices().DeviceHealth("core-sw1", CancellationToken.None));
        var data = root.GetProperty("data");

        var cpu = Assert.Single(data.GetProperty("open_alerts").EnumerateArray());
        Assert.Equal(8, cpu.GetProperty("fault_count").GetInt32());
        Assert.Equal("partly_acknowledged", cpu.GetProperty("state").GetString());
        Assert.Equal(8, data.GetProperty("open_faults").GetInt32());
        Assert.Contains("1 open alert", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_counts_the_devices_it_is_open_on()
    {
        var root = FakeLibreNms.Parse(await Rules().AlertRules(cancellationToken: CancellationToken.None));

        var cpu = root.GetProperty("data").GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("rule_id").GetInt32() == 5);
        Assert.Equal(1, cpu.GetProperty("open_alerts").GetInt32());
    }

    [Fact]
    public async Task One_core_clearing_isnt_the_alert_recovering()
    {
        // Two cores go into alert; one clears, then the other.
        _nms.Transport.On("logs/alertlog", """
            {"status":"ok","logs":[
              {"id":5,"rule_id":5,"device_id":1,"fault_id":9201,"state":0,"time_logged":"2026-10-17 09:30:00"},
              {"id":4,"rule_id":5,"device_id":1,"fault_id":9200,"state":0,"time_logged":"2026-10-17 09:20:00"},
              {"id":3,"rule_id":5,"device_id":1,"fault_id":null,"state":5,"time_logged":"2026-10-17 09:10:00"},
              {"id":2,"rule_id":5,"device_id":1,"fault_id":9201,"state":1,"time_logged":"2026-10-17 09:05:00"},
              {"id":1,"rule_id":5,"device_id":1,"fault_id":9200,"state":1,"time_logged":"2026-10-17 09:00:00"}
            ]}
            """);

        var root = FakeLibreNms.Parse(await Network().NetworkEvents(log: "alerts", hours: 6, cancellationToken: CancellationToken.None));

        var kinds = root.GetProperty("data").GetProperty("entries").EnumerateArray()
            .OrderBy(e => e.GetProperty("time").GetDateTime())
            .Select(e => e.GetProperty("kind").GetString())
            .ToList();
        Assert.Equal(new[] { "active", "fault_added", "changed", "fault_cleared", "recovered" }, kinds);
    }

    private static string Fault(int id, int device, int rule, int state, int? entityId, string severity, string ruleName)
    {
        var entity = entityId is null ? "\"entity_type\":null,\"entity_id\":null,\"entity_key\":\"\""
            : $"\"entity_type\":\"processor\",\"entity_id\":{entityId},\"entity_key\":\"processor:{entityId}\"";
        return $$"""
            {"id":{{id}},"device_id":{{device}},"rule_id":{{rule}},"state":{{state}},"timestamp":"2026-10-17 09:{{(10 + id % 50).ToString("00", CultureInfo.InvariantCulture)}}:00",
             "hostname":"x","severity":"{{severity}}","name":"{{ruleName}}",{{entity}}}
            """;
    }
}
