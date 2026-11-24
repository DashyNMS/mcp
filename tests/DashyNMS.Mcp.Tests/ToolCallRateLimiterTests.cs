using System.Text.Json;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

public sealed class ToolCallRateLimiterTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void Calls_over_the_limit_are_refused_until_the_oldest_is_a_minute_old()
    {
        var limiter = new ToolCallRateLimiter(2, _time);
        Assert.True(limiter.TryCall("a", out _));
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(limiter.TryCall("a", out _));

        Assert.False(limiter.TryCall("a", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(40), retryAfter);

        // Refused calls don't count, so waiting the time given works.
        _time.Advance(retryAfter);
        Assert.True(limiter.TryCall("a", out _));
        Assert.False(limiter.TryCall("a", out retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(20), retryAfter);
    }

    [Fact]
    public void One_client_does_not_use_up_anothers_calls()
    {
        var limiter = new ToolCallRateLimiter(1, _time);

        Assert.True(limiter.TryCall("a", out _));
        Assert.False(limiter.TryCall("a", out _));
        Assert.True(limiter.TryCall("b", out _));
    }

    [Fact]
    public async Task Parallel_calls_never_get_more_than_the_limit()
    {
        var limiter = new ToolCallRateLimiter(50, _time);

        var allowed = await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => Task.Run(() => limiter.TryCall("a", out var _))));

        Assert.Equal(50, allowed.Count(a => a));
    }

    [Fact]
    public async Task A_limited_call_is_told_to_wait_and_never_reaches_LibreNMS()
    {
        using var librenms = new FixtureServer(path => path switch
        {
            "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
            "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","status":1}]}"""),
            _ => null,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.RateLimitPerMinuteKey] = "1",
        });
        await server.InitializeAsync(cts.Token);

        var allowed = await server.CallToolAsync(2, "find_device", """{"query":"core"}""", cts.Token);
        Assert.Equal("ok", allowed.GetProperty("status").GetString());
        var asked = librenms.Requests.Length;

        var response = await server.RequestAsync(3, "tools/call", """{"name":"device_health","arguments":{"device":"core-sw1"}}""", cts.Token);

        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
        var limited = JsonDocument.Parse(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
        Assert.Equal("error", limited.GetProperty("status").GetString());
        Assert.Contains("the limit is 1 a minute", limited.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.InRange(limited.GetProperty("details").GetProperty("retry_after_seconds").GetInt32(), 1, 60);
        Assert.Equal(asked, librenms.Requests.Length);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
