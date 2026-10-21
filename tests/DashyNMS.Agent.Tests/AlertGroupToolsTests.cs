using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>active_alerts and alert_details on LibreNMS 26.10's per-entity faults (#75).</summary>
public sealed class AlertGroupToolsTests
{
    private readonly FakeLibreNms _nms = new();

    public AlertGroupToolsTests()
    {
        // Eight cores of core-sw1 over the CPU rule, two of them acknowledged,
        // and edge-sw3 down: what /faults returns on 26.10.
        var cores = Enumerable.Range(0, 8).Select(i => Fault(9200 + i, device: 1, rule: 5, i is 3 or 5 ? 2 : 1, "processor", 300 + i, $"CPU {i}", "warning", "High CPU"));
        var down = Fault(9300, device: 2, rule: 4, 1, null, null, null, "critical", "Device down");
        var json = $$"""{"status":"ok","alerts":[{{string.Join(",", cores.Append(down))}}]}""";

        _nms.Transport
            .On("faults", json)
            .On("faults/9203", $$"""{"status":"ok","alerts":[{{cores.ElementAt(3)}}]}""")
            .On("logs/alertlog/1", """{"status":"ok","logs":[]}""");
    }

    private AlertTools Alerts() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private RuleTools Rules() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task Eight_cores_are_one_alert_of_eight_faults()
    {
        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(cancellationToken: CancellationToken.None));
        var data = root.GetProperty("data");

        var totals = data.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("alert_groups").GetInt32());
        Assert.Equal(9, totals.GetProperty("faults").GetInt32());
        Assert.Equal(1, totals.GetProperty("partly_acknowledged").GetInt32());
        Assert.Contains("2 open alerts (9 faults)", root.GetProperty("summary").GetString(), StringComparison.Ordinal);

        var cpu = AlertsOn(data, 1).Single();
        Assert.Equal("device:1/rule:5", cpu.GetProperty("group_id").GetString());
        Assert.Equal(9200, cpu.GetProperty("alert_id").GetInt32());
        Assert.Equal(8, cpu.GetProperty("fault_count").GetInt32());
        Assert.Equal(2, cpu.GetProperty("acknowledged_count").GetInt32());
        Assert.Equal("partly_acknowledged", cpu.GetProperty("state").GetString());

        var faults = cpu.GetProperty("faults").EnumerateArray().ToList();
        Assert.Equal(8, faults.Count);
        Assert.All(faults, f => Assert.Equal("processor", f.GetProperty("entity_type").GetString()));
        Assert.Contains(faults, f => f.GetProperty("fault_id").GetInt32() == 9203 && f.GetProperty("entity").GetString() == "CPU 3" && f.GetProperty("state").GetString() == "acknowledged");
    }

    [Fact]
    public async Task A_single_fault_reads_like_an_alert()
    {
        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(device: "edge-sw3", cancellationToken: CancellationToken.None));

        var down = AlertsOn(root.GetProperty("data"), 2).Single();
        Assert.Equal(1, down.GetProperty("fault_count").GetInt32());
        Assert.False(down.TryGetProperty("faults", out _));
        Assert.Equal("active", down.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Pages_are_groups_in_a_stable_order()
    {
        var first = FakeLibreNms.Parse(await Alerts().ActiveAlerts(limit: 1, cancellationToken: CancellationToken.None)).GetProperty("data");
        var second = FakeLibreNms.Parse(await Alerts().ActiveAlerts(limit: 1, offset: 1, cancellationToken: CancellationToken.None)).GetProperty("data");

        Assert.Equal(2, first.GetProperty("page").GetProperty("total").GetInt32());

        // Worst first: the critical device-down group, then the CPU group with all its faults.
        Assert.Equal("device:2/rule:4", AlertsOn(first, 2).Single().GetProperty("group_id").GetString());
        Assert.Equal(8, AlertsOn(second, 1).Single().GetProperty("faults").GetArrayLength());
    }

    [Fact]
    public async Task Details_are_the_faults_own_and_name_its_group()
    {
        var root = FakeLibreNms.Parse(await Rules().AlertDetails(9203, CancellationToken.None));
        var data = root.GetProperty("data");

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("CPU 3", data.GetProperty("alert").GetProperty("entity").GetString());

        var why = data.GetProperty("why").GetProperty("faults").EnumerateArray().ToList();
        var matched = Assert.Single(why);
        Assert.Contains("CPU 3", matched.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(why, f => f.GetRawText().Contains("CPU 0", StringComparison.Ordinal));

        var group = data.GetProperty("group");
        Assert.Equal(8, group.GetProperty("fault_count").GetInt32());
        Assert.Contains("one of 8 faults", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Contains(_nms.Transport.Requests, r => r.StartsWith("faults/9203?state=0%2C1%2C2", StringComparison.Ordinal));
    }

    private static IEnumerable<JsonElement> AlertsOn(JsonElement data, int deviceId)
        => data.GetProperty("devices").EnumerateArray()
            .Where(d => d.GetProperty("device_id").GetInt32() == deviceId)
            .SelectMany(d => d.GetProperty("alerts").EnumerateArray());

    private static string Fault(int id, int device, int rule, int state, string? entityType, int? entityId, string? name, string severity, string ruleName)
    {
        var key = entityType is null ? "\"\"" : $"\"{entityType}:{entityId}\"";
        var details = name is null
            ? "null"
            : $$"""{"rule":[{"processor_id":{{entityId}},"processor_descr":"{{name}}","processor_usage":{{91 + id % 10}}}]}""";
        return $$"""
            {"id":{{id}},"device_id":{{device}},"rule_id":{{rule}},"state":{{state}},"timestamp":"2026-10-17 09:{{(10 + id % 50).ToString("00", CultureInfo.InvariantCulture)}}:00",
             "first_seen":"2026-10-17 09:00:00","hostname":"x","severity":"{{severity}}","name":"{{ruleName}}",
             "entity_type":{{(entityType is null ? "null" : $"\"{entityType}\"")}},"entity_id":{{(entityId?.ToString(CultureInfo.InvariantCulture) ?? "null")}},
             "entity_key":{{key}},"details":{{details}}}
            """;
    }
}
