using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// LibreNMS's API answering from recorded JSON, behind Core's real resource
/// APIs - so a tool test covers Core's URLs and parsing as well as the tool.
/// </summary>
internal sealed class FakeTransport : ILibreNmsTransport
{
    private readonly Dictionary<string, Func<string>> _routes = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public LibreNmsConnection? Connection => null;

    /// <summary>Answers a path (the URL before any query string) with this JSON.</summary>
    public FakeTransport On(string path, string json)
    {
        _routes[path] = () => json;
        return this;
    }

    /// <summary>Answers a path with an HTTP error, as the real transport throws one.</summary>
    public FakeTransport Fails(string path, HttpStatusCode status)
    {
        _routes[path] = () => throw new LibreNmsApiException($"HTTP {(int)status}", status);
        return this;
    }

    public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
    {
        Assert.Equal(HttpMethod.Get, method);
        Requests.Add(relativeUrl);
        var path = relativeUrl.Split('?')[0];
        return _routes.TryGetValue(path, out var answer)
            ? Task.FromResult(JsonDocument.Parse(answer()))
            : throw new LibreNmsApiException($"No fake for {path}", HttpStatusCode.NotFound);
    }

    public async Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, relativeUrl, null, cancellationToken);
        return document.RootElement.TryGetProperty(collectionProperty, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.Deserialize<List<T>>(LibreNmsJson.Options)!
            : [];
    }

    public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>Everything a tool needs, wired to a <see cref="FakeTransport"/> holding a small estate.</summary>
internal sealed class FakeLibreNms
{
    /// <summary>"Now" for every test: 10:00 UTC on 17 October 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    public FakeLibreNms()
    {
        Transport = Estate(new FakeTransport());
        Client = Substitute.For<ILibreNmsClient>();
        Client.Devices.Returns(new DevicesApi(Transport));
        Client.Alerts.Returns(new AlertsApi(Transport));
        Client.Rules.Returns(new AlertRulesApi(Transport));
        Client.Ports.Returns(new PortsApi(Transport));
        Client.Links.Returns(new LinksApi(Transport));
        Client.Health.Returns(new DeviceHealthApi(Transport));
        Client.Logs.Returns(new LogsApi(Transport));
        Client.Arp.Returns(new ArpApi(Transport));
        Client.DeviceGroups.Returns(new DeviceGroupsApi(Transport));

        Time = new FakeTimeProvider(Now);
        Directory = new DeviceDirectory(Client, Time);
        Resolver = new DeviceResolver(Directory);
        Runner = new ToolRunner(new Redactor(), Options, NullLogger<ToolRunner>.Instance);
        Timeline = new DeviceTimeline(Client, Options);
        Scope = new DeviceScope(Client, Directory, Time);
    }

    public FakeTransport Transport { get; }

    public ILibreNmsClient Client { get; }

    public FakeTimeProvider Time { get; }

    public AgentOptions Options { get; } = new();

    public DeviceDirectory Directory { get; }

    public DeviceResolver Resolver { get; }

    public ToolRunner Runner { get; }

    public DeviceTimeline Timeline { get; }

    public DeviceScope Scope { get; }

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static FakeTransport Estate(FakeTransport t) => t
        .On("devices", """
            {"status":"ok","devices":[
              {"device_id":1,"hostname":"core-sw1.example.com","sysName":"core-sw1","display":"Core switch 1","ip":"10.0.0.1",
               "os":"arubaos-cx","hardware":"6300M","location":"Paddock","status":1,"disabled":0,"ignore":0,"uptime":864000,
               "last_polled":"2026-10-17 09:55:00"},
              {"device_id":2,"hostname":"edge-sw3.example.com","sysName":"edge-sw3","display":"edge-sw3.example.com","ip":"10.0.0.3",
               "os":"procurve","hardware":"2930F","location":"Pit lane","status":0,"disabled":0,"ignore":0,"uptime":0,
               "last_polled":"2026-10-17 09:00:00"}
            ]}
            """)
        .On("devices/1", """
            {"status":"ok","devices":[
              {"device_id":1,"hostname":"core-sw1.example.com","sysName":"core-sw1","display":"Core switch 1","ip":"10.0.0.1",
               "os":"arubaos-cx","status":1,"uptime":864100,"last_polled":"2026-10-17 09:58:00"}
            ]}
            """)
        .On("devices/1/maintenance", """{"status":"ok","is_under_maintenance":false}""")
        .On("alerts", """
            {"status":"ok","alerts":[
              {"id":10,"device_id":1,"rule_id":3,"state":1,"timestamp":"2026-10-17 09:40:00","hostname":"core-sw1.example.com","severity":"critical","name":"Port down"},
              {"id":12,"device_id":1,"rule_id":5,"state":2,"timestamp":"2026-10-17 07:00:00","hostname":"core-sw1.example.com","severity":"warning","name":"High CPU","note":"Looking into it"},
              {"id":11,"device_id":2,"rule_id":4,"state":1,"timestamp":"2026-10-17 09:00:00","hostname":"edge-sw3.example.com","severity":"critical","name":"Device down"}
            ]}
            """)
        .On("rules", """{"status":"ok","rules":[{"id":3,"name":"Port down","severity":"critical"},{"id":4,"name":"Device down","severity":"warning"}]}""")
        .On("devices/1/health/processor", """{"status":"ok","graphs":[{"sensor_id":5},{"sensor_id":6}]}""")
        .On("devices/1/health/processor/5", """{"status":"ok","graphs":[{"processor_id":5,"device_id":1,"processor_descr":"CPU 0","processor_usage":37}]}""")
        .On("devices/1/health/processor/6", """{"status":"ok","graphs":[{"processor_id":6,"device_id":1,"processor_descr":"CPU 1","processor_usage":61}]}""")
        .On("devices/1/health/mempool", """{"status":"ok","graphs":[{"sensor_id":7}]}""")
        .On("devices/1/health/mempool/7", """{"status":"ok","graphs":[{"mempool_id":7,"device_id":1,"mempool_descr":"Memory","mempool_perc":48.25}]}""")
        .Fails("devices/1/health/storage", HttpStatusCode.Forbidden)
        .On("devices/1/ports", """
            {"status":"ok","ports":[
              {"port_id":101,"device_id":1,"ifName":"1/1/1","ifDescr":"1/1/1","ifAlias":"Uplink to edge-sw3","ifAdminStatus":"up","ifOperStatus":"up",
               "ifSpeed":10000000000,"ifDuplex":"fullDuplex","ifMtu":9198,"ifVlan":1,"ifInOctets_rate":12500000,"ifOutOctets_rate":2500000,
               "ifInErrors_rate":0,"ifOutErrors_rate":0},
              {"port_id":107,"device_id":1,"ifName":"1/1/7","ifDescr":"1/1/7","ifAlias":"Camera 7","ifAdminStatus":"up","ifOperStatus":"down","ifSpeed":1000000000},
              {"port_id":108,"device_id":1,"ifName":"1/1/8","ifDescr":"1/1/8","ifAlias":"Camera 8","ifAdminStatus":"up","ifOperStatus":"up",
               "ifSpeed":1000000000,"ifInErrors_rate":2.5,"ifOutErrors_rate":0,"ifInErrors_delta":750},
              {"port_id":109,"device_id":1,"ifName":"1/1/9","ifDescr":"1/1/9","ifAlias":"Spare","ifAdminStatus":"down","ifOperStatus":"down"}
            ]}
            """)
        .On("devices/1/links", """
            {"status":"ok","links":[
              {"id":1,"local_device_id":1,"local_port_id":101,"remote_hostname":"edge-sw3","remote_port":"49","remote_platform":"Aruba 2930F","protocol":"lldp","remote_device_id":2}
            ]}
            """)
        .On("resources/ip/arp/aa:bb:cc:dd:ee:ff", """
            {"status":"ok","arp":[{"id":1,"port_id":101,"device_id":1,"mac_address":"aabbccddeeff","ipv4_address":"10.0.10.50"}]}
            """)
        .On("logs/eventlog/1", """
            {"status":"ok","logs":[
              {"event_id":2,"device_id":1,"datetime":"2026-10-17 09:30:00","message":"Interface 1/1/7 went down","type":"interface","severity":4},
              {"event_id":1,"device_id":1,"datetime":"2026-10-10 12:00:00","message":"Device rebooted","type":"reboot","severity":3}
            ]}
            """)
        .On("logs/alertlog/1", """
            {"status":"ok","logs":[{"id":1,"rule_id":3,"device_id":1,"state":1,"time_logged":"2026-10-17 09:40:00"}]}
            """)
        // 09:00 to 09:10 UTC on 17 October 2026, as Unix timestamps.
        .On("devices/1/outages", """{"status":"ok","outages":[{"going_down":1792227600,"up_again":1792228200}]}""")
        .On("devicegroups", """
            {"status":"ok","groups":[
              {"id":1,"name":"Core switches","desc":"Distribution and core","type":"static"},
              {"id":2,"name":"Edge switches","type":"dynamic"},
              {"id":3,"name":"Access","type":"dynamic"}
            ]}
            """)
        .On("devicegroups/1", """{"status":"ok","devices":[{"device_id":1}]}""")
        .On("devicegroups/2", """{"status":"ok","devices":[{"device_id":2}]}""")
        .On("devicegroups/3", """{"status":"ok","devices":[]}""")
        // The whole network's alert log: device 1's port alert raised at
        // 09:40 and still open; device 2's raised and recovered overnight;
        // and one from a week ago, outside a 24-hour window.
        .On("logs/alertlog", """
            {"status":"ok","logs":[
              {"id":5,"rule_id":3,"device_id":1,"state":1,"time_logged":"2026-10-17 09:40:00"},
              {"id":4,"rule_id":4,"device_id":2,"state":0,"time_logged":"2026-10-17 03:30:00"},
              {"id":3,"rule_id":4,"device_id":2,"state":1,"time_logged":"2026-10-17 02:00:00"},
              {"id":2,"rule_id":4,"device_id":2,"state":0,"time_logged":"2026-10-10 05:00:00"},
              {"id":1,"rule_id":4,"device_id":2,"state":1,"time_logged":"2026-10-10 04:00:00"}
            ]}
            """);
}
