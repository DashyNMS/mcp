using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>Alert history and recoveries by fault_id on LibreNMS 26.10 (#76).</summary>
public sealed class AlertHistoryToolsTests
{
    private readonly FakeLibreNms _nms = new();

    private AlertTools Alerts() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private RuleTools Rules() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task Eight_cores_firing_together_are_one_raise()
    {
        // At 02:00 eight cores go into alert; they recover one by one from 03:00.
        var rows = Cores(state: 1, at: "2026-10-17 02:00:00", firstId: 100)
            .Concat(Enumerable.Range(0, 8).Select(i => Row(200 + i, 9200 + i, 0, $"2026-10-17 03:{i * 5:00}:00")))
            .ToList();
        Log("logs/alertlog", rows);

        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(hours: 12, cancellationToken: CancellationToken.None));

        var raised = RaisedOn(root, 1).Single();
        Assert.Equal(8, raised.GetProperty("fault_count").GetInt32());
        Assert.Equal("2026-10-17T03:35:00Z", raised.GetProperty("recovered_at").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        Assert.Contains("1 alert raised (8 faults)", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_core_recovering_doesnt_close_the_others()
    {
        // Eight cores at 02:00; only CPU 0 has recovered since.
        Log("logs/alertlog", Cores(state: 1, at: "2026-10-17 02:00:00", firstId: 100).Append(Row(200, 9200, 0, "2026-10-17 03:00:00")).ToList());

        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(hours: 12, cancellationToken: CancellationToken.None));

        var raised = RaisedOn(root, 1).Single();
        Assert.False(raised.TryGetProperty("recovered_at", out _));
    }

    [Fact]
    public async Task Legacy_and_fault_rows_mix_after_the_upgrade()
    {
        // Before the upgrade: a legacy firing and its recovery; after: two cores
        // (the 26.10 rule-level "worse" row between them isn't a raise).
        Log("logs/alertlog", new[]
        {
            Row(1, null, 1, "2026-10-17 01:00:00"),
            Row(2, null, 0, "2026-10-17 01:30:00"),
            Row(3, 9200, 1, "2026-10-17 04:00:00"),
            Row(4, 9201, 1, "2026-10-17 04:00:00"),
            Row(5, null, 3, "2026-10-17 04:00:00"),
            Row(6, 9201, 0, "2026-10-17 05:00:00"),
            Row(7, 9200, 0, "2026-10-17 05:10:00"),
        });

        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(hours: 12, cancellationToken: CancellationToken.None));

        var raised = RaisedOn(root, 1).ToList();
        Assert.Equal(2, raised.Count);
        Assert.Contains(raised, r => r.TryGetProperty("fault_count", out var n) && n.GetInt32() == 2);
    }

    [Fact]
    public async Task Earlier_firings_count_occurrences_not_cores()
    {
        // Three days ago, eight cores fired together; today again (this alert).
        var rows = Cores(state: 1, at: "2026-10-14 09:00:00", firstId: 100, firstFault: 9100)
            .Concat(Enumerable.Range(0, 8).Select(i => Row(150 + i, 9100 + i, 0, "2026-10-14 10:00:00")))
            .Concat(Cores(state: 1, at: "2026-10-17 09:00:00", firstId: 300, firstFault: 9200))
            .ToList();
        Log("logs/alertlog/1", rows);
        _nms.Transport.On("alerts/12", """
            {"status":"ok","alerts":[{"id":12,"device_id":1,"rule_id":5,"state":1,"timestamp":"2026-10-17 09:00:00","hostname":"core-sw1.example.com","severity":"warning","name":"High CPU"}]}
            """);

        var root = FakeLibreNms.Parse(await Rules().AlertDetails(12, CancellationToken.None));

        var history = root.GetProperty("data").GetProperty("history");
        Assert.Equal(1, history.GetProperty("fired_before_in_30_days").GetInt32());
        Assert.Contains("it fired once before", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_log_says_at_least()
    {
        // The log read is full (as deep as asked) and doesn't reach back 30 days.
        var filler = Enumerable.Range(0, RuleTools.HistoryDepth - 2)
            .Select(i => Row(1000 + i, null, 3, "2026-10-16 12:00:00"));
        var rows = new[] { Row(1, null, 1, "2026-10-15 09:00:00"), Row(2, null, 0, "2026-10-15 10:00:00") }
            .Concat(filler)
            .Append(Row(5000, null, 1, "2026-10-17 09:00:00"))
            .ToList();
        Log("logs/alertlog/1", rows);
        _nms.Transport.On("alerts/12", """
            {"status":"ok","alerts":[{"id":12,"device_id":1,"rule_id":5,"state":1,"timestamp":"2026-10-17 09:00:00","hostname":"core-sw1.example.com","severity":"warning","name":"High CPU"}]}
            """);

        var root = FakeLibreNms.Parse(await Rules().AlertDetails(12, CancellationToken.None));

        Assert.True(root.GetProperty("data").GetProperty("history").GetProperty("at_least").GetBoolean());
        Assert.Contains("at least once", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    private void Log(string path, IReadOnlyCollection<string> rows)
        => _nms.Transport.On(path, $$"""{"status":"ok","logs":[{{string.Join(",", rows.Reverse())}}]}""");

    private static IEnumerable<string> Cores(int state, string at, int firstId, int firstFault = 9200)
        => Enumerable.Range(0, 8).Select(i => Row(firstId + i, firstFault + i, state, at));

    private static string Row(int id, int? fault, int state, string at)
        => $$"""{"id":{{id}},"rule_id":5,"device_id":1,"fault_id":{{(fault?.ToString(CultureInfo.InvariantCulture) ?? "null")}},"state":{{state}},"time_logged":"{{at}}","hostname":"core-sw1.example.com"}""";

    private static IEnumerable<JsonElement> RaisedOn(JsonElement root, int deviceId)
        => root.GetProperty("data").GetProperty("devices").EnumerateArray()
            .Where(d => d.GetProperty("device_id").GetInt32() == deviceId)
            .SelectMany(d => d.GetProperty("alerts").EnumerateArray());
}
