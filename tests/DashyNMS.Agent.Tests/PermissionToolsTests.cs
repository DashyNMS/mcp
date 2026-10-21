using System.Net;
using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Tools;
using ModelContextProtocol.Protocol;

namespace DashyNMS.Agent.Tests;

/// <summary>LibreNMS 26.10's per-resource permissions in tool output (#79).</summary>
public sealed class PermissionToolsTests
{
    private readonly FakeLibreNms _nms = new();

    private DeviceTools Devices() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options);

    private HealthTools Health() => new(_nms.Sensors, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner);

    private GraphTools Graphs() => new(_nms.Client, _nms.Transport, _nms.Sensors, _nms.Resolver, _nms.Runner, _nms.Time);

    [Fact]
    public async Task Alerts_that_couldnt_be_read_are_unknown_not_none()
    {
        _nms.Transport.Fails("alerts", HttpStatusCode.InternalServerError);

        var root = FakeLibreNms.Parse(await Devices().DeviceHealth("core-sw1", CancellationToken.None));

        Assert.Contains("open alerts unknown", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("0 open alerts", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var unavailable = root.GetProperty("data").GetProperty("unavailable").EnumerateArray().Single(u => u.GetProperty("source").GetString() == "alerts");
        Assert.Equal("failed", unavailable.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_refused_source_is_denied_not_failed()
    {
        _nms.Transport.Fails("alerts", HttpStatusCode.Forbidden);

        var root = FakeLibreNms.Parse(await Devices().DeviceHealth("core-sw1", CancellationToken.None));

        var unavailable = root.GetProperty("data").GetProperty("unavailable").EnumerateArray().Single(u => u.GetProperty("source").GetString() == "alerts");
        Assert.Equal("denied", unavailable.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task One_device_the_account_cant_read_doesnt_fail_the_rest()
    {
        _nms.Transport
            .Fails("resources/sensors", HttpStatusCode.Forbidden)
            .Fails("devices/2/health", HttpStatusCode.Forbidden);

        var root = FakeLibreNms.Parse(await Health().HealthOverview(location: "Pit lane", cancellationToken: CancellationToken.None));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Contains("1 device couldn't be read", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_graph_the_account_cant_see_is_denied_not_broken()
    {
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><xhtml:div>No Authorization</xhtml:div></foreignObject></svg>""";
        var envelope = $$$"""{"status":"ok","image":{"image":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}}}","content-type":"image/svg+xml"}}""";
        _nms.Transport
            .On("devices/1/graphs", """{"status":"ok","graphs":[{"desc":"Traffic","name":"device_bits"}]}""")
            .On("devices/1/health", """{"status":"ok","graphs":[]}""")
            .On("devices/1/wireless", """{"status":"ok","graphs":[]}""")
            .On("devices/1/device_bits", envelope);

        var result = await Graphs().Graph("core-sw1", cancellationToken: CancellationToken.None);

        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var root = FakeLibreNms.Parse(text);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Contains("isn't allowed to see this graph", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Content, c => c is ImageContentBlock);
    }

    [Fact]
    public async Task Totals_say_whose_they_are()
    {
        var root = FakeLibreNms.Parse(await new NetworkTools(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time)
            .NetworkOverview(CancellationToken.None));

        Assert.StartsWith("2 devices visible to this account", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }
}
