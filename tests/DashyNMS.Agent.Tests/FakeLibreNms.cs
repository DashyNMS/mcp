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

    public FakeLibreNms(AgentOptions? options = null)
    {
        Options = options ?? new();
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
        Client.Sensors.Returns(new SensorsApi(Transport));
        Client.Vlans.Returns(new VlansApi(Transport));
        Client.Fdb.Returns(new FdbApi(Transport));
        Client.Routing.Returns(new RoutingApi(Transport));
        Client.AlertTemplates.Returns(new AlertTemplatesApi(Transport));
        Client.Locations.Returns(new LocationsApi(Transport));
        Client.System.Returns(new SystemApi(Transport));

        Time = new FakeTimeProvider(Now);
        Directory = new DeviceDirectory(Client, Time);
        Resolver = new DeviceResolver(Directory);
        Runner = new ToolRunner(new Redactor(), Options, NullLogger<ToolRunner>.Instance);
        Timeline = new DeviceTimeline(Client, Options);
        Scope = new DeviceScope(Client, Directory, Time);
        Fleet = new FleetCache(Client, Time);
        Sensors = new SensorSource(Fleet, Transport, Time);
    }

    public FakeTransport Transport { get; }

    public ILibreNmsClient Client { get; }

    public FakeTimeProvider Time { get; }

    public AgentOptions Options { get; }

    public DeviceDirectory Directory { get; }

    public DeviceResolver Resolver { get; }

    public ToolRunner Runner { get; }

    public DeviceTimeline Timeline { get; }

    public DeviceScope Scope { get; }

    public FleetCache Fleet { get; }

    public SensorSource Sensors { get; }

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
        .On("rules", """
            {"status":"ok","rules":[
              {"id":3,"name":"Port down","severity":"critical","rule":"ports.ifOperStatus = \"down\" AND ports.ifAdminStatus = \"up\"","builder":"{\"condition\":\"AND\",\"rules\":[{\"id\":\"ports.ifOperStatus\",\"field\":\"ports.ifOperStatus\",\"operator\":\"equal\",\"value\":\"down\"},{\"id\":\"ports.ifAdminStatus\",\"field\":\"ports.ifAdminStatus\",\"operator\":\"equal\",\"value\":\"up\"}],\"valid\":true}","devices":[],"groups":[1],"locations":[],"disabled":0,
               "proc":"https://wiki.example.com/port-down","notes":"Check the patching first."},
              {"id":4,"name":"Device down","severity":"warning","rule":"macros.device_down = 1","devices":[2],"groups":[],"locations":[],"disabled":0},
              {"id":5,"name":"High CPU","severity":"warning","rule":"processors.processor_usage >= 90","devices":[],"groups":[],"locations":[1],"disabled":0},
              {"id":6,"name":"Old test rule","severity":"critical","rule":"devices.status = 0","devices":[],"groups":[],"locations":[],"disabled":1}
            ]}
            """)
        .On("alerts/10", """
            {"status":"ok","alerts":[{"id":10,"device_id":1,"rule_id":3,"state":1,"timestamp":"2026-10-17 09:40:00","hostname":"core-sw1.example.com","severity":"critical","name":"Port down"}]}
            """)
        .On("alert_templates", """{"status":"ok","alert_templates":[{"id":1,"name":"Port template","alert_rules":[3]}]}""")
        .On("resources/locations", """{"status":"ok","locations":[{"id":1,"location":"Paddock"},{"id":2,"location":"Pit lane"}]}""")
        .On("system", """{"status":"ok","system":[{"local_ver":"26.10.0"}]}""")
        // Every port in the network, as the Top widgets' snapshot reads it.
        .On("ports", """
            {"status":"ok","ports":[
              {"port_id":101,"device_id":1,"ifName":"1/1/1","ifAlias":"Uplink to edge-sw3","ifAdminStatus":"up","ifOperStatus":"up","ifSpeed":10000000000,"ifInOctets_rate":125000000,"ifOutOctets_rate":25000000,"ifInErrors_rate":0,"ifOutErrors_rate":0},
              {"port_id":107,"device_id":1,"ifName":"1/1/7","ifAlias":"Camera 7","ifAdminStatus":"up","ifOperStatus":"down","ifSpeed":1000000000},
              {"port_id":108,"device_id":1,"ifName":"1/1/8","ifAlias":"Camera 8","ifAdminStatus":"up","ifOperStatus":"up","ifSpeed":1000000000,"ifInOctets_rate":1250000,"ifOutOctets_rate":250000,"ifInErrors_rate":2.5,"ifOutErrors_rate":0},
              {"port_id":249,"device_id":2,"ifName":"49","ifAdminStatus":"up","ifOperStatus":"up","ifSpeed":10000000000,"ifInOctets_rate":25000000,"ifOutOctets_rate":100000000,"ifInErrors_rate":0.5,"ifOutErrors_rate":1}
            ]}
            """)
        .On("logs/eventlog", """
            {"status":"ok","logs":[
              {"event_id":12,"device_id":2,"datetime":"2026-10-17 09:50:00","message":"Device status changed to Down","type":"system","severity":5},
              {"event_id":2,"device_id":1,"datetime":"2026-10-17 09:30:00","message":"Interface 1/1/7 went down","type":"interface","severity":4},
              {"event_id":11,"device_id":2,"datetime":"2026-10-17 06:00:00","message":"Device rebooted","type":"reboot","severity":3},
              {"event_id":1,"device_id":1,"datetime":"2026-10-10 12:00:00","message":"Device rebooted","type":"reboot","severity":3}
            ]}
            """)
        .On("devices/1/health/processor", """{"status":"ok","graphs":[{"sensor_id":5},{"sensor_id":6}]}""")
        .On("devices/1/health/processor/5", """{"status":"ok","graphs":[{"processor_id":5,"device_id":1,"processor_descr":"CPU 0","processor_usage":37}]}""")
        .On("devices/1/health/processor/6", """{"status":"ok","graphs":[{"processor_id":6,"device_id":1,"processor_descr":"CPU 1","processor_usage":61}]}""")
        .On("devices/1/health/mempool", """{"status":"ok","graphs":[{"sensor_id":7}]}""")
        .On("devices/1/health/mempool/7", """{"status":"ok","graphs":[{"mempool_id":7,"device_id":1,"mempool_descr":"Memory","mempool_perc":48.25}]}""")
        .Fails("devices/1/health/storage", HttpStatusCode.Forbidden)
        .On("devices/1/ports", """
            {"status":"ok","ports":[
              {"port_id":101,"device_id":1,"ifName":"1/1/1","ifDescr":"1/1/1","ifAlias":"Uplink to edge-sw3","ifAdminStatus":"up","ifOperStatus":"up",
               "ifSpeed":10000000000,"ifDuplex":"fullDuplex","ifMtu":9198,"ifVlan":1,"vlans":[{"vlan":1,"untagged":true},{"vlan":120,"untagged":false}],"ifInOctets_rate":12500000,"ifOutOctets_rate":2500000,
               "ifInErrors_rate":0,"ifOutErrors_rate":0},
              {"port_id":107,"device_id":1,"ifName":"1/1/7","ifDescr":"1/1/7","ifAlias":"Camera 7","ifAdminStatus":"up","ifOperStatus":"down","ifSpeed":1000000000,"vlans":[{"vlan":120,"untagged":true}]},
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
        // Device 1's alert log: today's port alert with the row that tripped
        // it, and two earlier firings of the same rule (one 40 days ago, so
        // outside the 30 days alert_details counts).
        .On("logs/alertlog/1", """
            {"status":"ok","logs":[
              {"id":1,"rule_id":3,"device_id":1,"state":1,"time_logged":"2026-10-17 09:40:00",
               "details":{"contacts":{},"rule":[{"device_id":1,"hostname":"core-sw1.example.com","port_id":107,"ifName":"1/1/7","ifAlias":"Camera 7","ifOperStatus":"down","ifAdminStatus":"up"}]}},
              {"id":-1,"rule_id":3,"device_id":1,"state":0,"time_logged":"2026-10-08 10:00:00"},
              {"id":-2,"rule_id":3,"device_id":1,"state":1,"time_logged":"2026-10-08 09:00:00"},
              {"id":-3,"rule_id":3,"device_id":1,"state":1,"time_logged":"2026-09-07 09:00:00"}
            ]}
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
            """)
        // Every sensor in the network. Device 1: a module over its own limit,
        // a fan stopped below its low limit, an optic below desktop's default
        // dBm threshold, an unplugged optic's sentinel reading, and PoE
        // budget sensors (ProCurve names). Device 2: fine, plus a reading
        // with no limits at all.
        .On("resources/sensors", """
            {"status":"ok","sensors":[
              {"sensor_id":1,"device_id":1,"sensor_class":"temperature","sensor_descr":"Chassis","sensor_current":48,"sensor_limit":70},
              {"sensor_id":2,"device_id":1,"sensor_class":"temperature","sensor_descr":"Module 2","sensor_current":72,"sensor_limit":70,"sensor_limit_warn":65},
              {"sensor_id":3,"device_id":1,"sensor_class":"fanspeed","sensor_descr":"Fan 2","sensor_current":0,"sensor_limit_low":500},
              {"sensor_id":4,"device_id":1,"sensor_class":"dbm","sensor_descr":"1/1/49 Rx Power","sensor_current":-15.5},
              {"sensor_id":5,"device_id":1,"sensor_class":"dbm","sensor_descr":"1/1/50 Rx Power","sensor_current":-40},
              {"sensor_id":6,"device_id":1,"sensor_class":"power","sensor_descr":"PoE Power Total","sensor_index":"1","sensor_current":370},
              {"sensor_id":7,"device_id":1,"sensor_class":"power","sensor_descr":"PoE Power Used","sensor_index":"1","sensor_current":120.5},
              {"sensor_id":8,"device_id":2,"sensor_class":"temperature","sensor_descr":"Chassis","sensor_current":31,"sensor_limit":70},
              {"sensor_id":9,"device_id":2,"sensor_class":"voltage","sensor_descr":"PSU 1","sensor_current":12.1}
            ]}
            """)
        // Device 1 reports edge-sw3 on its uplink and an unknown camera on
        // 1/1/8; edge-sw3 (device 2) reports nothing itself.
        .On("resources/links", """
            {"status":"ok","links":[
              {"id":1,"local_device_id":1,"local_port_id":101,"remote_hostname":"edge-sw3","remote_port":"49","remote_platform":"Aruba 2930F","protocol":"lldp","remote_device_id":2},
              {"id":2,"local_device_id":1,"local_port_id":108,"remote_hostname":"cam-08","remote_port":"eth0","remote_platform":"IP camera","protocol":"lldp","remote_device_id":0}
            ]}
            """)
        .On("devices/2/ports", """{"status":"ok","ports":[{"port_id":249,"device_id":2,"ifName":"49","ifAdminStatus":"up","ifOperStatus":"up"}]}""")
        .On("inventory/1/all", """
            {"status":"ok","inventory":[
              {"entPhysical_id":1,"entPhysicalIndex":1,"entPhysicalClass":"chassis","entPhysicalName":"Chassis","entPhysicalModelName":"JL658A","entPhysicalSerialNum":"SG00TEST01","entPhysicalSoftwareRev":"FL.10.13","entPhysicalIsFRU":"false"},
              {"entPhysical_id":2,"entPhysicalIndex":2,"entPhysicalClass":"powerSupply","entPhysicalName":"PSU 1","entPhysicalModelName":"JL085A","entPhysicalSerialNum":"PS00TEST01","entPhysicalIsFRU":"true"},
              {"entPhysical_id":3,"entPhysicalIndex":3,"entPhysicalClass":"port","entPhysicalName":"1/1/1"}
            ]}
            """)
        .On("resources/vlans", """
            {"status":"ok","vlans":[
              {"vlan_id":10,"device_id":1,"vlan_vlan":1,"vlan_name":"default"},
              {"vlan_id":11,"device_id":1,"vlan_vlan":120,"vlan_name":"cameras"},
              {"vlan_id":20,"device_id":2,"vlan_vlan":1,"vlan_name":"default"}
            ]}
            """)
        .On("devices/1/fdb", """{"status":"ok","ports_fdb":[{"ports_fdb_id":1,"port_id":107,"device_id":1,"mac_address":"aabbccdd0107","vlan_id":11,"updated_at":"2026-10-17 09:00:00"}]}""")
        .On("resources/ip/arp/all", """{"status":"ok","arp":[{"id":2,"port_id":101,"device_id":1,"mac_address":"aabbccdd0003","ipv4_address":"10.0.0.3"}]}""")
        .On("devices/1/wireless-sensors", """{"status":"ok","wireless_sensors":[]}""")
        .On("bgp", """
            {"status":"ok","bgp_sessions":[
              {"bgpPeer_id":1,"device_id":1,"bgpPeerIdentifier":"192.0.2.1","bgpPeerRemoteAs":64512,"bgpPeerState":"established","bgpPeerAdminStatus":"running","bgpPeerFsmEstablishedTime":86400},
              {"bgpPeer_id":2,"device_id":1,"bgpPeerIdentifier":"192.0.2.9","bgpPeerRemoteAs":64513,"bgpPeerState":"idle","bgpPeerAdminStatus":"running","bgpPeerLastErrorText":"Hold Timer Expired"}
            ]}
            """)
        .On("ospf", """{"status":"ok","ospf_neighbours":[]}""")
        .On("ospfv3", """{"status":"ok","ospfv3_neighbours":[]}""")
        .On("routing/vrf", """{"status":"ok","vrfs":[]}""")
        .On("devices/1/availability", """
            {"status":"ok","availability":[
              {"duration":86400,"availability_perc":100},
              {"duration":604800,"availability_perc":99.9406},
              {"duration":2592000,"availability_perc":99.986}
            ]}
            """)
        // The per-device route, for tokens refused the network-wide list:
        // types, then each type's sensor IDs, then each sensor's own row.
        .On("devices/1/health", """
            {"status":"ok","graphs":[{"desc":"Temperature","name":"device_temperature"},{"desc":"Fanspeed","name":"device_fanspeed"},{"desc":"Processor","name":"device_processor"}]}
            """)
        .On("devices/1/health/device_temperature", """{"status":"ok","graphs":[{"sensor_id":1,"desc":"Chassis"},{"sensor_id":2,"desc":"Module 2"}]}""")
        .On("devices/1/health/device_temperature/1", """{"status":"ok","graphs":[{"sensor_id":1,"device_id":1,"sensor_class":"temperature","sensor_descr":"Chassis","sensor_current":48,"sensor_limit":70,"sensor_deleted":0}]}""")
        .On("devices/1/health/device_temperature/2", """{"status":"ok","graphs":[{"sensor_id":2,"device_id":1,"sensor_class":"temperature","sensor_descr":"Module 2","sensor_current":72,"sensor_limit":70,"sensor_limit_warn":65,"sensor_deleted":0}]}""")
        .On("devices/1/health/device_fanspeed", """{"status":"ok","graphs":[{"sensor_id":3,"desc":"Fan 2"}]}""")
        .On("devices/1/health/device_fanspeed/3", """{"status":"ok","graphs":[{"sensor_id":3,"device_id":1,"sensor_class":"fanspeed","sensor_descr":"Fan 2","sensor_current":0,"sensor_limit_low":500,"sensor_deleted":0}]}""")
        .On("devices/1/ip", """
            {"status":"ok","addresses":[
              {"port_id":101,"ipv4_address":"10.0.0.1","ipv4_prefixlen":24},
              {"port_id":101,"ipv6_address":"2001:0db8:0000:0000:0000:0000:0000:0001","ipv6_compressed":"2001:db8::1","ipv6_prefixlen":64}
            ]}
            """);
}
