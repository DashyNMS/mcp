using System.Net;
using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The cap on requests in flight, under load: the real server, many tool calls
/// at once and a LibreNMS that answers slowly (#38). The calls queue for longer
/// than the request timeout, so a wait counted towards it would fail them.
/// </summary>
public sealed class RequestLoadTests
{
    private const int Cap = 3;
    private const int Calls = 60;

    [Fact]
    public async Task Parallel_calls_never_exceed_the_cap_or_time_out()
    {
        using var librenms = new SlowLibreNms(TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.LibreNmsTimeout] = "5",
            [McpOptions.MaxConcurrentRequestsKey] = Cap.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await server.InitializeAsync(cts.Token);

        var results = await server.CallToolsAsync(
            Enumerable.Range(0, Calls).Select(i => (100 + i, "device_health", $$"""{"device":"{{i + 1}}"}""")).ToList(),
            cts.Token);

        Assert.Equal(Calls, results.Count);
        Assert.All(results.Values, result =>
        {
            Assert.Equal("ok", result.GetProperty("status").GetString());
            Assert.DoesNotContain("timed out", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        });

        Assert.InRange(librenms.MostAtOnce, 2, Cap);
        Assert.True(librenms.Answered > Calls, "each call should have made requests of its own");

        // The calls at the back of the queue tell the model to slow down.
        Assert.Contains(results.Values, r => r.TryGetProperty("throttling", out var t) && t.GetProperty("waited_seconds").GetDouble() >= 5);
    }

    /// <summary>A LibreNMS of <see cref="Calls"/> devices that takes a while over each request and counts how many it's holding.</summary>
    private sealed class SlowLibreNms : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TimeSpan _delay;
        private readonly object _sync = new();
        private int _atOnce;

        public SlowLibreNms(TimeSpan delay)
        {
            _delay = delay;
            Root = FixtureServer.NothingListening();
            _listener.Prefixes.Add(Root.ToString());
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Root { get; }

        public int MostAtOnce { get; private set; }

        public int Answered { get; private set; }

        private async Task ServeAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => AnswerAsync(context));
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            lock (_sync)
            {
                MostAtOnce = Math.Max(MostAtOnce, ++_atOnce);
            }

            await Task.Delay(_delay);

            // Before the answer goes out, so the client can't have its next request here first.
            lock (_sync)
            {
                _atOnce--;
                Answered++;
            }

            try
            {
                var (status, body) = Body(context.Request.Url!.AbsolutePath);
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client gave up or the test ended.
            }
        }

        private static (int Status, string Body) Body(string path)
        {
            if (path == "/api/v0/system")
            {
                return (200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}""");
            }

            if (path == "/api/v0/devices")
            {
                return (200, JsonSerializer.Serialize(new { status = "ok", devices = Enumerable.Range(1, Calls).Select(Device) }));
            }

            const string device = "/api/v0/devices/";
            if (path.StartsWith(device, StringComparison.Ordinal) && int.TryParse(path[device.Length..], out var id))
            {
                return (200, JsonSerializer.Serialize(new { status = "ok", devices = new[] { Device(id) } }));
            }

            return (404, """{"status":"error","message":"Nothing here"}""");
        }

        private static object Device(int id)
            => new { device_id = id, hostname = $"switch{id}.example.net", ip = $"192.0.2.{id}", status = 1, disabled = 0, ignore = 0 };

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
