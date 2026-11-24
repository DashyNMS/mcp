using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Health;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// Network sensor questions on a large network whose token may not read the
/// network-wide list (#71): read device by device for a limited time, saying
/// how far that got, until asking again has covered everything.
/// </summary>
public sealed class SensorSweepTests
{
    private const int Devices = 60;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly LargeNetwork _network = new(Devices);

    [Fact]
    public async Task A_network_read_in_time_is_answered_in_full_without_the_network_wide_list()
    {
        var root = await Overview();

        var coverage = root.GetProperty("data").GetProperty("coverage");
        Assert.Equal(Devices, coverage.GetProperty("devices").GetInt32());
        // Device 2 is down: its last readings are old ones.
        Assert.Equal(Devices - 1, coverage.GetProperty("read").GetInt32());
        Assert.Equal(1, coverage.GetProperty("down").GetInt32());
        Assert.True(coverage.GetProperty("complete").GetBoolean());
        Assert.False(coverage.TryGetProperty("read_everything", out _));
        Assert.Equal($"{Devices - 1} sensor readings checked on devices visible to this account: 1 critical, 0 warning. " +
            $"Read device by device: {Devices - 1} of {Devices} devices (1 down).", root.GetProperty("summary").GetString());
        Assert.DoesNotContain(_network.Requests, r => r.StartsWith("devices/2/", StringComparison.Ordinal));
        Assert.Equal("edge-sw7.example.net", root.GetProperty("data").GetProperty("readings")[0].GetProperty("device").GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task Running_out_of_time_says_what_is_missing_and_what_would_cover_it()
    {
        _network.Reached = _network.StopTheClockAfter(10, Budget);

        var root = await Overview();

        var coverage = root.GetProperty("data").GetProperty("coverage");
        Assert.False(coverage.GetProperty("complete").GetBoolean());
        var read = coverage.GetProperty("read").GetInt32();
        Assert.InRange(read, 1, Devices - 2);
        Assert.Equal(Devices - 1 - read, coverage.GetProperty("not_reached").GetInt32());
        Assert.Contains("sensor.view", coverage.GetProperty("read_everything").GetString(), StringComparison.Ordinal);
        Assert.Contains("Ask again", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_again_carries_on_until_every_device_is_covered()
    {
        _network.Reached = _network.StopTheClockAfter(10, Budget);
        Assert.False((await Overview()).GetProperty("data").GetProperty("coverage").GetProperty("complete").GetBoolean());

        _network.Reached = null;
        var before = _network.Requests.Count;
        var coverage = (await Overview()).GetProperty("data").GetProperty("coverage");

        Assert.True(coverage.GetProperty("complete").GetBoolean());
        Assert.Equal(Devices - 1, coverage.GetProperty("read").GetInt32());
        Assert.True(_network.Requests.Count > before);

        // Nothing read the first time was read again, only the few devices it was part way through.
        var again = _network.Requests.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key.Split('/')[1]).Distinct().Count();
        Assert.InRange(again, 0, AgentOptions.DefaultMaxConcurrentRequests / 2);

        // Long enough after, it's all read afresh.
        _network.Time.Advance(SensorSource.SweepLifetime);
        var first = _network.Requests.Count;
        await Overview();
        Assert.Equal(first + 3 * (Devices - 1), _network.Requests.Count);
    }

    [Fact]
    public async Task Alerting_devices_are_read_first()
    {
        _network.Alerting = [50];
        _network.Reached = _network.StopTheClockAfter(4, Budget);

        await Overview();

        Assert.StartsWith("devices/50/", _network.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_type_asks_only_for_that_type()
    {
        var root = await Overview(type: "temperature");

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("data").GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.All(_network.Requests, r => Assert.Contains("/health/device_temperature", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_type_already_read_with_the_rest_is_not_read_again()
    {
        await Overview();
        var before = _network.Requests.Count;

        await Overview(type: "temperature");

        Assert.Equal(before, _network.Requests.Count);
    }

    [Fact]
    public async Task The_newer_of_two_readings_is_used_whichever_question_it_was_read_for()
    {
        await Overview(type: "temperature");
        _network.Time.Advance(TimeSpan.FromMinutes(1));
        _network.Hot = 9;
        await Overview();

        var readings = (await Overview(type: "temperature")).GetProperty("data").GetProperty("readings");

        Assert.Equal("edge-sw9.example.net", Assert.Single(readings.EnumerateArray()).GetProperty("device").GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task Devices_the_account_may_not_read_or_LibreNMS_cannot_answer_for_are_counted_apart()
    {
        _network.Denied = [3, 4];
        _network.Broken = [5];

        var root = await Overview();

        var coverage = root.GetProperty("data").GetProperty("coverage");
        Assert.Equal(2, coverage.GetProperty("denied").GetInt32());
        Assert.Equal(1, coverage.GetProperty("failed").GetInt32());
        Assert.Equal(0, coverage.GetProperty("not_reached").GetInt32());
        Assert.False(coverage.GetProperty("complete").GetBoolean());
        Assert.Equal(
            Devices,
            new[] { "read", "down", "denied", "failed", "not_reached" }.Sum(p => coverage.GetProperty(p).GetInt32()));
    }

    [Fact]
    public async Task LibreNms_failing_for_everything_is_an_error_not_an_empty_answer()
    {
        _network.Broken = Enumerable.Range(1, Devices).ToHashSet();

        var root = await Overview();

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Never_more_requests_at_once_than_half_of_what_the_host_allows()
    {
        _network.Slow = true;

        await Overview();

        Assert.InRange(_network.MostAtOnce, 1, AgentOptions.DefaultMaxConcurrentRequests / 2);
    }

    [Fact]
    public async Task A_host_without_a_budget_still_refuses_a_scope_too_big_to_read_in_full()
    {
        var overview = HealthOverview.Create(_network.Client, _network, _network.Time, new AgentOptions());

        var refused = await Assert.ThrowsAsync<FleetSensorsRefusedException>(() => overview.ReadAsync(new HealthOverviewRequest()));

        Assert.Contains("sensor.view", refused.Message, StringComparison.Ordinal);
        Assert.Contains("25 devices or fewer", refused.Message, StringComparison.Ordinal);
    }

    private async Task<JsonElement> Overview(string? type = null)
    {
        _tools ??= Create();
        return FakeLibreNms.Parse(await _tools.HealthOverview(type, cancellationToken: CancellationToken.None));
    }

    private HealthTools? _tools;

    private HealthTools Create()
    {
        var options = new AgentOptions { SensorSweepBudget = Budget };
        var directory = new DeviceDirectory(_network.Client, _network.Time);
        var fleet = new FleetCache(_network.Client, _network.Time);
        var runner = new ToolRunner(new Redactor(), options, NullLogger<ToolRunner>.Instance) { Returned = OutputContract.Check };
        return new HealthTools(new SensorSource(fleet.GetSensorsAsync, _network, _network.Time), directory, new DeviceResolver(directory),
            new DeviceScope(_network.Client, directory, _network.Time), runner, _network.Client);
    }

    /// <summary>Devices with one temperature sensor each, one of them hot, on a server that refuses the network-wide list.</summary>
    private sealed class LargeNetwork : ILibreNmsTransport
    {
        private readonly int _devices;
        private readonly ConcurrentQueue<string> _requests = new();
        private int _now;
        private int _mostAtOnce;

        public LargeNetwork(int devices)
        {
            _devices = devices;
            Client = Substitute.For<ILibreNmsClient>();
            var apis = new LibreNmsResourceApis(this);
            Client.Devices.Returns(apis.Devices);
            Client.Sensors.Returns(apis.Sensors);
            Client.Alerts.Returns(apis.Alerts);
            Client.DeviceGroups.Returns(apis.DeviceGroups);
        }

        public ILibreNmsClient Client { get; }

        public FakeTimeProvider Time { get; } = new(FakeLibreNms.Now);

        /// <summary>The per-device requests, in order.</summary>
        public IReadOnlyList<string> Requests => _requests.ToArray();

        public int MostAtOnce => _mostAtOnce;

        public HashSet<int> Alerting { get; set; } = [];

        public HashSet<int> Denied { get; set; } = [];

        public HashSet<int> Broken { get; set; } = [];

        public bool Slow { get; set; }

        /// <summary>The one device that's hot.</summary>
        public int Hot { get; set; } = 7;

        /// <summary>Called before each per-device request, with how many there have been.</summary>
        public Action<int>? Reached { get; set; }

        public LibreNmsConnection? Connection => null;

        public Action<int> StopTheClockAfter(int requests, TimeSpan budget) => count =>
        {
            if (count == requests)
            {
                Time.Advance(budget);
            }
        };

        public async Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
        {
            using var document = await SendAsync(HttpMethod.Get, relativeUrl, null, cancellationToken);
            return document.RootElement.TryGetProperty(collectionProperty, out var items) && items.ValueKind == JsonValueKind.Array
                ? items.Deserialize<List<T>>(LibreNmsJson.Options)!
                : [];
        }

        public async Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = relativeUrl.Split('?')[0].Split('/');
            if (parts is ["resources", "sensors"])
            {
                throw new LibreNmsApiException("Forbidden", HttpStatusCode.Forbidden);
            }

            if (parts is not ["devices", var id, "health", ..])
            {
                return JsonDocument.Parse(Estate(parts[0]));
            }

            _requests.Enqueue(relativeUrl);
            Reached?.Invoke(_requests.Count);
            cancellationToken.ThrowIfCancellationRequested();
            var device = int.Parse(id, CultureInfo.InvariantCulture);
            if (Denied.Contains(device))
            {
                throw new LibreNmsApiException("Forbidden", HttpStatusCode.Forbidden);
            }

            if (Broken.Contains(device))
            {
                throw new LibreNmsApiException("Broken", HttpStatusCode.InternalServerError);
            }

            if (Slow)
            {
                var now = Interlocked.Increment(ref _now);
                InterlockedMax(ref _mostAtOnce, now);
                await Task.Delay(5, cancellationToken);
                Interlocked.Decrement(ref _now);
            }

            return JsonDocument.Parse(parts.Length switch
            {
                3 => """{"graphs":[{"name":"device_temperature"},{"name":"device_processor"}]}""",
                4 => $$"""{"graphs":[{"sensor_id":{{device}},"desc":"Inlet"}]}""",
                _ => $$"""{"graphs":[{"sensor_id":{{device}},"device_id":{{device}},"sensor_class":"temperature","sensor_descr":"Inlet","sensor_current":{{(device == Hot ? 95 : 21)}},"sensor_limit":80}]}""",
            });
        }

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private string Estate(string path) => path switch
        {
            "devices" => JsonSerializer.Serialize(new
            {
                devices = Enumerable.Range(1, _devices).Select(id => new { device_id = id, hostname = $"edge-sw{id}.example.net", status = id == 2 ? 0 : 1, disabled = 0, ignore = 0 }),
            }),
            "alerts" or "faults" => JsonSerializer.Serialize(new
            {
                alerts = Alerting.Select(id => new { id, device_id = id, rule_id = 1, state = 1 }),
                faults = Alerting.Select(id => new { id, device_id = id, rule_id = 1, state = 1 }),
            }),
            _ => "{}",
        };

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }
}
