using System.Diagnostics;
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

        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "dashynms-mcp.dll")}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.Environment[McpOptions.LibreNmsUrl] = librenms.Url;
        start.Environment[McpOptions.LibreNmsToken] = "smoke-test-token";

        using var server = Process.Start(start)!;
        var stderr = new StringBuilder();
        server.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
        server.BeginErrorReadLine();

        try
        {
            async Task<JsonElement> Request(int id, string method, string parameters)
            {
                await server.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
                await server.StandardInput.FlushAsync(cts.Token);
                while (true)
                {
                    var line = await server.StandardOutput.ReadLineAsync(cts.Token)
                        ?? throw new InvalidOperationException($"Server exited. stderr:\n{stderr}");
                    var message = JsonDocument.Parse(line).RootElement;
                    if (message.TryGetProperty("id", out var got) && got.GetInt32() == id)
                    {
                        return message;
                    }
                }
            }

            var init = await Request(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}""");
            var serverInfo = init.GetProperty("result").GetProperty("serverInfo");
            Assert.Equal("dashynms", serverInfo.GetProperty("name").GetString());
            Assert.Equal("DashyNMS", serverInfo.GetProperty("title").GetString());

            // The logo, embedded, so a client shows it rather than a letter.
            var icon = serverInfo.GetProperty("icons")[0];
            Assert.StartsWith("data:image/png;base64,iVBOR", icon.GetProperty("src").GetString(), StringComparison.Ordinal);
            Assert.Equal("image/png", icon.GetProperty("mimeType").GetString());
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var list = await Request(2, "tools/list", "{}");
            var tools = list.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
            Assert.Equal(21, tools.Count);
            Assert.All(tools, tool =>
            {
                Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), tool.GetProperty("name").GetString());
                Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            });

            var call = await Request(3, "tools/call", """{"name":"find_device","arguments":{"query":"core"}}""");
            var text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            var result = JsonDocument.Parse(text).RootElement;
            Assert.Equal("ok", result.GetProperty("status").GetString());
            Assert.Equal("core-sw1.example.com", result.GetProperty("data").GetProperty("devices")[0].GetProperty("hostname").GetString());

            Assert.Contains("X-Auth-Token: smoke-test-token", librenms.Headers, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("smoke-test-token", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
        }
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
