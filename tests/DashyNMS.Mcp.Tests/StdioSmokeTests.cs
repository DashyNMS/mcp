using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The real server process, over stdio, against a stand-in LibreNMS: proves
/// startup, the MCP handshake, the tool list a client sees, and one call.
/// </summary>
public sealed class StdioSmokeTests
{
    [Fact]
    public async Task Server_lists_its_read_only_tools_and_answers_a_call()
    {
        using var librenms = new StandInLibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Url,
            [McpOptions.LibreNmsToken] = "smoke-test-token",
        });

        var init = await server.InitializeAsync(cts.Token);
        var serverInfo = init.GetProperty("result").GetProperty("serverInfo");
        Assert.Equal("dashynms", serverInfo.GetProperty("name").GetString());
        Assert.Equal("DashyNMS", serverInfo.GetProperty("title").GetString());

        // The logo, embedded, so a client shows it rather than a letter.
        var icon = serverInfo.GetProperty("icons")[0];
        Assert.StartsWith("data:image/png;base64,iVBOR", icon.GetProperty("src").GetString(), StringComparison.Ordinal);
        Assert.Equal("image/png", icon.GetProperty("mimeType").GetString());

        var list = await server.RequestAsync(2, "tools/list", "{}", cts.Token);
        var tools = list.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(24, tools.Count);
        ListedTools.AssertUnchanged(list.GetProperty("result").GetProperty("tools"));
        CatalogueMatch.AssertEqual(list.GetProperty("result").GetProperty("tools"));
        Assert.All(tools, tool =>
        {
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), tool.GetProperty("name").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
        });

        // Every parameter keeps its type, though text ones are read leniently (#13).
        Assert.All(tools, tool =>
        {
            foreach (var parameter in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
            {
                Assert.True(parameter.Value.TryGetProperty("type", out _), $"{tool.GetProperty("name").GetString()}.{parameter.Name}");
            }
        });

        // A model sending an ID as a number, where the schema says text, still gets an answer.
        var numeric = await server.RequestAsync(4, "tools/call", """{"name":"find_device","arguments":{"query":1}}""", cts.Token);
        Assert.False(numeric.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean(), numeric.ToString());
        Assert.Equal("ok", JsonDocument.Parse(numeric.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("status").GetString());

        var call = await server.RequestAsync(3, "tools/call", """{"name":"find_device","arguments":{"query":"core"}}""", cts.Token);
        var text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var result = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal("core-sw1.example.com", result.GetProperty("data").GetProperty("devices")[0].GetProperty("hostname").GetString());

        Assert.Contains("X-Auth-Token: smoke-test-token", librenms.Headers, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("smoke-test-token", await server.StopAsync(cts.Token), StringComparison.Ordinal);
    }

    /// <summary>Just enough HTTP for the server's startup check and a device list.</summary>
    private sealed class StandInLibreNms : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public StandInLibreNms()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public List<string> Headers { get; } = [];

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var request = await reader.ReadLineAsync() ?? string.Empty;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                {
                    lock (Headers)
                    {
                        Headers.Add(header);
                    }
                }

                var path = request.Split(' ')[1];
                var body = path.StartsWith("/api/v0/system", StringComparison.Ordinal)
                    ? """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""
                    : path.StartsWith("/api/v0/devices", StringComparison.Ordinal)
                        ? """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.com","sysName":"core-sw1","ip":"10.0.0.1","status":1}]}"""
                        : """{"status":"error","message":"not here"}""";
                var status = body.Contains("\"error\"", StringComparison.Ordinal) ? "404 Not Found" : "200 OK";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(bytes);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
