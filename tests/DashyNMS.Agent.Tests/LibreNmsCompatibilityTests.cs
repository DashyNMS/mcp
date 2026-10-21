using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The #80 scenarios: the tools against Core's sanitised LibreNMS 26.9.1.1 and
/// 26.10.0 responses (Fixtures/LibreNMS), as a client would ask about them.
/// </summary>
public sealed class LibreNmsCompatibilityTests
{
    private readonly FakeLibreNms _nms = new();

    private AlertTools Alerts() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private RuleTools Rules() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private NetworkTools Network() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task Eight_cores_are_one_alert_and_each_cores_details_are_its_own()
    {
        var cores = UseEightCores();

        var root = Parse(await Alerts().ActiveAlerts(cancellationToken: CancellationToken.None));
        var totals = root.GetProperty("data").GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("alert_groups").GetInt32());
        Assert.Equal(8, totals.GetProperty("faults").GetInt32());

        foreach (var (id, i) in cores.Select((id, i) => (id, i)))
        {
            var details = Parse(await Rules().AlertDetails(id, CancellationToken.None));
            var why = details.GetProperty("data").GetProperty("why").GetRawText();

            Assert.Equal($"CPU {i}", details.GetProperty("data").GetProperty("alert").GetProperty("entity").GetString());
            Assert.Contains($"CPU {i}", why, StringComparison.Ordinal);
            Assert.DoesNotContain($"CPU {(i + 1) % 8}", why, StringComparison.Ordinal);
            Assert.Equal(8, details.GetProperty("data").GetProperty("group").GetProperty("fault_count").GetInt32());
        }
    }

    [Fact]
    public async Task Cores_recovering_one_by_one_never_read_as_all_eight_recovering()
    {
        var cores = UseEightCores();
        var log = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            log.Add(Row(100 + i, cores[i], 1, "2026-10-17 06:00:00"));
        }

        for (var recovered = 1; recovered <= 8; recovered++)
        {
            // The server's open list no longer has the recovered cores.
            _nms.Transport.On("faults", Envelope("alerts", FaultRows().Skip(recovered)));
            log.Add(Row(200 + recovered, cores[recovered - 1], 0, $"2026-10-17 07:{recovered:00}:00"));
            _nms.Transport.On("logs/alertlog", Envelope("logs", Enumerable.Reverse(log)));

            var open = Parse(await Alerts().ActiveAlerts(cancellationToken: CancellationToken.None));
            var events = Parse(await Network().NetworkEvents(log: "alerts", hours: 6, cancellationToken: CancellationToken.None));
            var latest = events.GetProperty("data").GetProperty("entries")[0].GetProperty("kind").GetString();

            if (recovered < 8)
            {
                Assert.Equal(8 - recovered, open.GetProperty("data").GetProperty("totals").GetProperty("faults").GetInt32());
                Assert.Equal(1, open.GetProperty("data").GetProperty("totals").GetProperty("alert_groups").GetInt32());
                Assert.Equal("fault_cleared", latest);
            }
            else
            {
                Assert.StartsWith("No open alerts", open.GetProperty("summary").GetString(), StringComparison.Ordinal);
                Assert.Equal("recovered", latest);
            }
        }
    }

    [Fact]
    public async Task Mixed_legacy_and_fault_history_counts_occurrences()
    {
        // Core's log: a legacy firing on 2 October, two cores on 12 October,
        // and the eight cores open now - with a rule-level "worse" row between.
        UseEightCores();
        _nms.Transport.On("logs/alertlog/7", Fixture("26.10.0/alertlog-eight-cores.json"));

        var details = Parse(await Rules().AlertDetails(9203, CancellationToken.None));

        Assert.Equal(2, details.GetProperty("data").GetProperty("history").GetProperty("fired_before_in_30_days").GetInt32());
    }

    [Fact]
    public async Task A_26_9_server_still_reads_as_alerts()
    {
        _nms.Transport
            .On("devices", Fixture("26.9.1.1/devices.json"))
            .On("alerts", Fixture("26.9.1.1/alerts.json"));

        var root = Parse(await Alerts().ActiveAlerts(cancellationToken: CancellationToken.None));
        var totals = root.GetProperty("data").GetProperty("totals");

        Assert.Equal(totals.GetProperty("alert_groups").GetInt32(), totals.GetProperty("faults").GetInt32());
        Assert.DoesNotContain(_nms.Transport.Requests, r => r.StartsWith("faults?state=1", StringComparison.Ordinal));
        var alert = root.GetProperty("data").GetProperty("devices")[0].GetProperty("alerts")[0];
        Assert.Equal(1, alert.GetProperty("fault_count").GetInt32());
    }

    [Fact]
    public async Task Thousands_of_faults_page_deterministically_with_bounded_requests()
    {
        var rows = Enumerable.Range(0, 6000).Select(i => $$"""
            {"id":{{100000 + i}},"device_id":{{1000 + i % 500}},"rule_id":{{1 + i % 3}},"state":{{(i % 7 == 0 ? 2 : 1)}},"timestamp":"2026-10-17 09:{{(i % 60).ToString("00", CultureInfo.InvariantCulture)}}:00",
             "hostname":"d{{i % 500}}.example.net","severity":"{{(i % 2 == 0 ? "critical" : "warning")}}","name":"Rule {{1 + i % 3}}",
             "entity_type":"port","entity_id":{{i}},"entity_key":"port:{{i}}"}
            """);
        _nms.Transport.On("faults", Envelope("alerts", rows));

        var timer = Stopwatch.StartNew();
        var seen = new List<string>();
        int? offset = 0;
        var calls = 0;
        var firstPage = 0;
        while (offset is { } next)
        {
            var root = Parse(await Alerts().ActiveAlerts(offset: next, limit: 100, cancellationToken: CancellationToken.None));
            Assert.False(root.TryGetProperty("truncated", out _), "A page was cut by the result cap.");
            var page = root.GetProperty("data");
            seen.AddRange(page.GetProperty("devices").EnumerateArray().SelectMany(d => d.GetProperty("alerts").EnumerateArray()).Select(a => a.GetProperty("group_id").GetString()!));
            offset = page.GetProperty("page").TryGetProperty("next_offset", out var n) ? n.GetInt32() : null;
            firstPage = firstPage == 0 ? seen.Count : firstPage;
            calls++;
        }

        timer.Stop();

        // 1,500 rules-on-devices, each exactly once, in pages that fit the result cap.
        Assert.Equal(1500, seen.Count);
        Assert.Equal(1500, seen.Distinct().Count());

        // The same first page twice: a stable order.
        var again = Parse(await Alerts().ActiveAlerts(limit: 100, cancellationToken: CancellationToken.None)).GetProperty("data");
        Assert.Equal(seen.Take(firstPage), again.GetProperty("devices").EnumerateArray().SelectMany(d => d.GetProperty("alerts").EnumerateArray()).Select(a => a.GetProperty("group_id").GetString()!));

        // One list request per call, however many faults.
        Assert.Equal(calls + 1, _nms.Transport.Requests.Count(r => r.StartsWith("faults?state=1", StringComparison.Ordinal)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"Took {timer.Elapsed}");
    }

    /// <summary>Core's eight-core estate: device 7's eight cores, each answerable by ID. Returns their fault IDs.</summary>
    private List<int> UseEightCores()
    {
        _nms.Transport
            .On("devices", Fixture("26.10.0/devices.json"))
            .On("faults", Fixture("26.10.0/faults-eight-cores.json"))
            .On("logs/alertlog/7", """{"status":"ok","logs":[]}""");

        var ids = new List<int>();
        foreach (var row in FaultRows())
        {
            var id = JsonDocument.Parse(row).RootElement.GetProperty("id").GetInt32();
            ids.Add(id);
            _nms.Transport.On($"faults/{id}", Envelope("alerts", [row]));
        }

        return ids;
    }

    private static IEnumerable<string> FaultRows()
        => JsonDocument.Parse(Fixture("26.10.0/faults-eight-cores.json")).RootElement.GetProperty("alerts").EnumerateArray().Select(a => a.GetRawText()).ToList();

    private static string Fixture(string file)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibreNMS", file));

    private static string Envelope(string property, IEnumerable<string> rows)
        => $$"""{"status":"ok","{{property}}":[{{string.Join(",", rows)}}]}""";

    private static string Row(int id, int fault, int state, string at)
        => $$"""{"id":{{id}},"rule_id":3,"device_id":7,"fault_id":{{fault}},"state":{{state}},"time_logged":"{{at}}"}""";

    private static JsonElement Parse(string json) => FakeLibreNms.Parse(json);
}
