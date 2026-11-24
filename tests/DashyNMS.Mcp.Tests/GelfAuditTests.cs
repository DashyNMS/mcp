using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Tests;
using DashyNMS.Mcp.Auditing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Mcp.Tests;

/// <summary>Sending the audit trail to Graylog (#18): the message, the address, and that a Graylog that's down costs nothing.</summary>
public sealed class GelfAuditTests
{
    private static AuditEntry Entry(string tool = "find_device") => new(
        DateTimeOffset.UnixEpoch.AddSeconds(1_800_000_000).AddMilliseconds(250),
        "http", "alice", "claude-ai", "1.0", tool, new JsonObject { ["query"] = "core" }, 2, AuditStatus.Ok, 412, TimeSpan.FromMilliseconds(38));

    [Fact]
    public void A_message_is_GELF_with_the_entrys_own_fields()
    {
        var root = JsonDocument.Parse(GelfAuditSink.Message(Entry(), "mcp-host")).RootElement;

        Assert.Equal("1.1", root.GetProperty("version").GetString());
        Assert.Equal("mcp-host", root.GetProperty("host").GetString());
        Assert.Equal("MCP tool call find_device: ok", root.GetProperty("short_message").GetString());
        Assert.Equal(1_800_000_000.25, root.GetProperty("timestamp").GetDouble());
        Assert.Equal(6, root.GetProperty("level").GetInt32());
        Assert.Equal("alice", root.GetProperty("_token_label").GetString());
        Assert.Equal("""{"query":"core"}""", root.GetProperty("_arguments").GetString());
        Assert.Equal(2, root.GetProperty("_undeclared_arguments").GetInt32());
        Assert.Equal(412, root.GetProperty("_result_bytes").GetInt32());
        Assert.Equal(38, root.GetProperty("_duration_ms").GetInt32());

        // GELF takes text and numbers only, and reserves _id.
        Assert.All(root.EnumerateObject(), p => Assert.True(p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number, p.Name));
        Assert.False(root.TryGetProperty("_id", out _));
    }

    [Theory]
    [InlineData("https://graylog.example.net:12201/gelf", "https://graylog.example.net:12201/gelf")]
    [InlineData("http://graylog.example.net:12201", "http://graylog.example.net:12201/gelf")]
    [InlineData("https://logs.example.net/in/gelf", "https://logs.example.net/in/gelf")]
    public void An_http_address_defaults_to_Graylogs_own_path(string text, string expected)
    {
        var target = GelfTarget.TryParse(text, []);

        Assert.Equal(new Uri(expected), target!.Address);
        Assert.False(target.IsUdp);
    }

    [Theory]
    [InlineData("udp://graylog.example.net", 12201)]
    [InlineData("udp://graylog.example.net:5000", 5000)]
    public void A_udp_address_defaults_to_Graylogs_own_port(string text, int port)
    {
        var target = GelfTarget.TryParse(text, []);

        Assert.True(target!.IsUdp);
        Assert.Equal("graylog.example.net", target.Address.Host);
        Assert.Equal(port, target.Address.Port);
    }

    [Theory]
    [InlineData("graylog.example.net")]
    [InlineData("tcp://graylog.example.net:12201")]
    [InlineData("https://user:synthetic-secret@graylog.example.net/gelf")]
    [InlineData("https://graylog.example.net/gelf?token=synthetic-secret")]
    [InlineData("udp://graylog.example.net/gelf")]
    public void Any_other_address_stops_startup_without_being_repeated(string text)
    {
        var values = new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = "https://librenms.example.net",
            [McpOptions.LibreNmsToken] = "synthetic-token",
            [McpOptions.AuditGelfUrl] = text,
        };

        Assert.Null(McpOptions.TryLoad(values.GetValueOrDefault, out var errors));
        var error = Assert.Single(errors);
        Assert.StartsWith("AUDIT_GELF_URL isn't a GELF input's address.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://graylog.example.net:12201/gelf", true)]
    [InlineData("udp://graylog.example.net", true)]
    [InlineData("https://graylog.example.net/gelf", false)]
    [InlineData("http://127.0.0.1:12201/gelf", false)]
    public void An_unencrypted_address_is_warned_about(string text, bool warned)
    {
        var values = new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = "https://librenms.example.net",
            [McpOptions.LibreNmsToken] = "synthetic-token",
            [McpOptions.AuditGelfUrl] = text,
        };

        var options = McpOptions.TryLoad(values.GetValueOrDefault, out _);

        Assert.Equal(warned, options!.Warnings.Any(w => w.StartsWith("AUDIT_GELF_URL", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Entries_reach_a_udp_input()
    {
        using var input = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)input.Client.LocalEndPoint!).Port;
        using var sink = new GelfAuditSink(GelfTarget.TryParse($"udp://127.0.0.1:{port}", [])!, NullLogger<GelfAuditSink>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sink.StartAsync(cts.Token);

        sink.Write(Entry());

        var received = await input.ReceiveAsync(cts.Token);
        Assert.Equal("find_device", JsonDocument.Parse(received.Buffer).RootElement.GetProperty("_tool").GetString());
        await sink.StopAsync(cts.Token);
    }

    [Fact]
    public void The_biggest_entry_fits_one_udp_packet()
    {
        var redactor = new AuditRedactor(new DashyNMS.Agent.Redaction.Redactor(), []);
        var schema = JsonDocument.Parse("""{"properties":{"a":{},"b":{}}}""").RootElement;

        // Every character escaped to six, as close under the limit as it goes.
        var arguments = JsonSerializer.SerializeToElement(new { a = new string('é', 240), b = new string('é', 240) });
        var (recorded, _) = redactor.Arguments(arguments.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value)), schema);
        var entry = Entry() with { Arguments = recorded, ClientName = new string('é', 100), ClientVersion = new string('é', 100) };

        // Graylog takes 8192 bytes; past it a message has to be chunked.
        Assert.InRange(GelfAuditSink.Message(entry, new string('h', 255)).Length, 1, 8192);
    }

    [Fact]
    public async Task Every_call_reaches_an_http_input_as_well_as_stderr()
    {
        var received = new ConcurrentQueue<string>();
        using var graylog = new FixtureServer(
            _ => null,
            requestAnswer: request =>
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                received.Enqueue(request.HttpMethod + " " + request.Url!.AbsolutePath + " " + reader.ReadToEnd());
                return new Answer(202, "");
            },
            authenticated: _ => true);
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, graylog.Root.ToString());
        await server.InitializeAsync(cts.Token);

        await server.CallToolAsync(2, "find_device", """{"query":"core"}""", cts.Token);
        await server.CallToolAsync(3, "find_device", $$"""{"query":"{{FixtureServer.Token}}"}""", cts.Token);

        // Stopping sends whatever is still queued.
        var stderr = await server.StopAsync(cts.Token);
        Assert.Equal(2, received.Count);
        Assert.All(received, r => Assert.StartsWith("POST /gelf {", r, StringComparison.Ordinal));
        Assert.DoesNotContain(received, r => r.Contains(FixtureServer.Token, StringComparison.Ordinal));
        Assert.Equal(2, stderr.Split('\n').Count(l => l.StartsWith("""{"event":"tool_call",""", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_Graylog_that_cannot_be_reached_is_reported_once_and_holds_nothing_up()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, FixtureServer.NothingListening().ToString());
        await server.InitializeAsync(cts.Token);

        for (var id = 2; id < 7; id++)
        {
            var result = await server.CallToolAsync(id, "find_device", """{"query":"core"}""", cts.Token);
            Assert.Equal("ok", result.GetProperty("status").GetString());
        }

        var stderr = await server.StopAsync(cts.Token);
        Assert.Equal(5, stderr.Split('\n').Count(l => l.StartsWith("""{"event":"tool_call",""", StringComparison.Ordinal)));
        Assert.Equal(1, stderr.Split("Audit entries aren't reaching Graylog").Length - 1);
        Assert.Contains("5 audit entries weren't sent to Graylog", stderr, StringComparison.Ordinal);
    }

    private static FixtureServer LibreNms() => new(path => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","status":1}]}"""),
        _ => null,
    });

    private static StdioServer Start(FixtureServer librenms, string gelf) => StdioServer.Start(new Dictionary<string, string?>
    {
        [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
        [McpOptions.LibreNmsToken] = FixtureServer.Token,
        [McpOptions.AuditGelfUrl] = gelf,
    });
}
