using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The standalone server - the same program desktop Intelligence bundles -
/// reports the totals Core counts for the same alerts (#80). Core's count is
/// what the desktop app's badge and Alerts tab use.
/// </summary>
public sealed class TotalsParityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_server_counts_26_10_faults_as_Core_does()
    {
        var faults = string.Join(",", Enumerable.Range(0, 8).Select(i => $$"""
            {"id":{{9200 + i}},"device_id":7,"rule_id":3,"state":{{(i == 3 ? 2 : 1)}},"timestamp":"2026-10-20 09:00:00","hostname":"core-sw-01.example.net",
             "severity":"critical","name":"Processor usage over 90%","entity_type":"processor","entity_id":{{300 + i}},"entity_key":"processor:{{300 + i}}"}
            """).Append("""
            {"id":9300,"device_id":8,"rule_id":4,"state":1,"timestamp":"2026-10-20 08:00:00","hostname":"cam-gate.example.net","severity":"warning","name":"Device down","entity_key":""}
            """));
        var faultList = $$"""{"status":"ok","alerts":[{{faults}}]}""";

        using var librenms = new StandInLibreNms(path =>
            path.StartsWith("/api/v0/system", StringComparison.Ordinal) ? """{"status":"ok","system":[{"local_ver":"26.10.1"}]}"""
            : path.StartsWith("/api/v0/devices", StringComparison.Ordinal) ? """{"status":"ok","devices":[{"device_id":7,"hostname":"core-sw-01.example.net","status":1},{"device_id":8,"hostname":"cam-gate.example.net","status":0}]}"""
            : path.StartsWith("/api/v0/faults", StringComparison.Ordinal) ? faultList
            : null);

        var server = await CallAsync(librenms.Url, "token", "active_alerts", "{}");
        var expected = OpenAlertTotals.From(JsonDocument.Parse(faultList).RootElement.GetProperty("alerts").Deserialize<List<Alert>>(LibreNmsJson.Options)!);

        var totals = server.GetProperty("data").GetProperty("totals");
        Assert.Equal(expected.AlertGroups, totals.GetProperty("alert_groups").GetInt32());
        Assert.Equal(expected.Faults, totals.GetProperty("faults").GetInt32());
        Assert.Equal(expected.Critical, totals.GetProperty("critical").GetInt32());
        Assert.Equal(expected.PartlyAcknowledged, totals.GetProperty("partly_acknowledged").GetInt32());
        Assert.Equal(2, expected.AlertGroups);
        Assert.Equal(9, expected.Faults);
    }

    /// <summary>
    /// The live half of #80, read-only: set DASHYNMS_LIVE_URL and
    /// DASHYNMS_LIVE_TOKEN (and DASHYNMS_LIVE_ACCOUNT to label the run) and the
    /// server's totals are compared with Core's on a real LibreNMS. Prints
    /// counts only - nothing identifiable - for the issue.
    /// </summary>
    [Fact]
    public async Task Live_the_server_and_Core_agree_on_a_real_LibreNMS()
    {
        var url = Environment.GetEnvironmentVariable("DASHYNMS_LIVE_URL");
        var token = Environment.GetEnvironmentVariable("DASHYNMS_LIVE_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            return; // Not configured: CI never talks to a real server.
        }

        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
        transport.Configure(new LibreNmsConnection(new Uri(url), token, timeoutSeconds: 120));
        var apis = new LibreNmsResourceApis(transport);
        var core = OpenAlertTotals.From(await apis.Alerts.ListAsync(AlertQuery.Open));
        var version = (await apis.System.GetAsync()).LocalVersion;

        var server = await CallAsync(url, token, "active_alerts", """{"limit":1}""");
        var overview = await CallAsync(url, token, "network_overview", "{}");
        var totals = server.GetProperty("data").GetProperty("totals");
        var overviewAlerts = overview.GetProperty("data").GetProperty("alerts");

        output.WriteLine($"LibreNMS {version}, account {Environment.GetEnvironmentVariable("DASHYNMS_LIVE_ACCOUNT") ?? "unlabelled"}");
        output.WriteLine($"Core: {core.AlertGroups} alerts, {core.Faults} faults, {core.Critical} critical, {core.Warning} warning, {core.PartlyAcknowledged} partly acknowledged");
        output.WriteLine($"active_alerts: {totals.GetProperty("alert_groups").GetInt32()} alerts, {totals.GetProperty("faults").GetInt32()} faults");
        output.WriteLine($"network_overview: {overviewAlerts.GetProperty("open").GetInt32()} alerts, {overviewAlerts.GetProperty("faults").GetInt32()} faults");

        // Alerts can open or clear between the calls, so allow a little drift.
        Assert.InRange(totals.GetProperty("alert_groups").GetInt32(), core.AlertGroups - 3, core.AlertGroups + 3);
        Assert.InRange(overviewAlerts.GetProperty("open").GetInt32(), core.AlertGroups - 3, core.AlertGroups + 3);
    }

    /// <summary>Starts the real server over stdio, calls one tool, and returns its result.</summary>
    private static async Task<JsonElement> CallAsync(string url, string token, string tool, string arguments)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "dashynms-mcp.dll")}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.Environment[McpOptions.LibreNmsUrl] = url;
        start.Environment[McpOptions.LibreNmsToken] = token;

        using var server = Process.Start(start)!;
        server.ErrorDataReceived += (_, _) => { };
        server.BeginErrorReadLine();
        try
        {
            async Task<JsonElement> Request(int id, string method, string parameters)
            {
                await server.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
                await server.StandardInput.FlushAsync(cts.Token);
                while (true)
                {
                    var line = await server.StandardOutput.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("The server exited.");
                    var message = JsonDocument.Parse(line).RootElement;
                    if (message.TryGetProperty("id", out var got) && got.GetInt32() == id)
                    {
                        return message;
                    }
                }
            }

            await Request(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"parity","version":"1"}}""");
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            var call = await Request(2, "tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}""");
            var text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            var result = JsonDocument.Parse(text).RootElement;
            Assert.Equal("ok", result.GetProperty("status").GetString());
            return result;
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>Just enough HTTP for the server: each path answered by <c>answer</c>, or a 404.</summary>
    private sealed class StandInLibreNms : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string?> _answer;

        public StandInLibreNms(Func<string, string?> answer)
        {
            _answer = answer;
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var request = await reader.ReadLineAsync() ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }

                var body = _answer(request.Split(' ')[1]);
                var status = body is null ? "404 Not Found" : "200 OK";
                var bytes = Encoding.UTF8.GetBytes(body ?? """{"status":"error","message":"not here"}""");
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
