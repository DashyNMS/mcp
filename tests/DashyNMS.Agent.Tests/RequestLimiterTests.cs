using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Requests;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

/// <summary>The cap on requests in flight to a source (#38), and how a tool's result reports a wait.</summary>
public sealed class RequestLimiterTests
{
    private readonly FakeTimeProvider _time = new();

    private RequestLimiter Limiter(int limit) => new(limit, _time);

    private static ToolRunner Runner() => new(new Redactor(), new AgentOptions(), NullLogger<ToolRunner>.Instance);

    [Fact]
    public async Task Requests_over_the_limit_wait_for_one_to_finish()
    {
        var limiter = Limiter(2);
        var first = await limiter.EnterAsync(CancellationToken.None);
        using var second = await limiter.EnterAsync(CancellationToken.None);

        var third = limiter.EnterAsync(CancellationToken.None).AsTask();
        Assert.False(third.IsCompleted);

        first.Dispose();
        using var entered = await third.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Giving_a_slot_back_twice_does_not_raise_the_limit()
    {
        var limiter = Limiter(1);
        var first = await limiter.EnterAsync(CancellationToken.None);
        first.Dispose();
        first.Dispose();

        using var second = await limiter.EnterAsync(CancellationToken.None);
        Assert.False(limiter.EnterAsync(CancellationToken.None).AsTask().IsCompleted);
    }

    [Fact]
    public async Task A_cancelled_wait_takes_no_slot()
    {
        var limiter = Limiter(1);
        var first = await limiter.EnterAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = limiter.EnterAsync(cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        first.Dispose();
        using var next = await limiter.EnterAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_already_cancelled_request_is_refused_even_with_a_slot_free()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Limiter(1).EnterAsync(cancel.Token).AsTask());
    }

    [Theory]
    [InlineData(2400, 2.4)]
    [InlineData(1000, 1.0)]
    public async Task A_result_whose_requests_queued_says_so(int milliseconds, double reported)
    {
        var result = await RunQueuedAsync(TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal("ok", result.GetProperty("status").GetString());
        var throttling = result.GetProperty("throttling");
        Assert.Equal(reported, throttling.GetProperty("waited_seconds").GetDouble());
        Assert.Contains("fewer calls", throttling.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_brief_wait_is_not_reported()
    {
        var result = await RunQueuedAsync(TimeSpan.FromMilliseconds(999));

        Assert.False(result.TryGetProperty("throttling", out _));
    }

    [Fact]
    public async Task A_failed_call_reports_its_wait_too()
    {
        var result = await RunQueuedAsync(TimeSpan.FromSeconds(5), fail: true);

        Assert.Equal("error", result.GetProperty("status").GetString());
        Assert.True(result.TryGetProperty("throttling", out _));
    }

    [Fact]
    public async Task One_calls_wait_is_not_reported_by_another()
    {
        await RunQueuedAsync(TimeSpan.FromSeconds(5));

        var other = JsonDocument.Parse(await Runner().RunAsync("test_tool", _ => Task.FromResult(ToolResult.Ok("Fine.", null)), CancellationToken.None)).RootElement;
        Assert.False(other.TryGetProperty("throttling", out _));
        Assert.Null(ToolCallWaits.Current);
    }

    [Fact]
    public async Task The_longest_of_several_waits_is_reported()
    {
        var waits = new ToolCallWaits();
        await Task.WhenAll(Enumerable.Range(1, 200).Select(i => Task.Run(() => waits.Record(TimeSpan.FromSeconds(i)))));

        Assert.Equal(TimeSpan.FromSeconds(200), waits.Longest);
    }

    [Fact]
    public async Task A_source_limiting_its_own_requests_is_named()
    {
        var json = await Runner().RunAsync("test_tool", _ => throw new LibreNmsApiException("raw", HttpStatusCode.TooManyRequests), CancellationToken.None);

        var message = JsonDocument.Parse(json).RootElement.GetProperty("message").GetString();
        Assert.StartsWith("LibreNMS: it's limiting how often it can be asked (HTTP 429).", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_source_gets_a_limiter_of_its_own()
    {
        using var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent(new AgentOptions { MaxConcurrentRequests = 3 }).BuildServiceProvider();

        var gates = new[]
        {
            provider.GetRequiredService<LibreNmsTransport>().RequestGate,
            provider.GetRequiredService<UnimusApi>().RequestGate,
            provider.GetRequiredService<GraylogApi>().RequestGate,
        };

        Assert.All(gates, g => Assert.IsType<RequestLimiter>(g));
        Assert.Equal(3, gates.Distinct().Count());
        Assert.Same(provider.GetRequiredService<ServerFailover>(), provider.GetRequiredService<LibreNmsTransport>().Failover);
    }

    /// <summary>A tool call whose one request waits <paramref name="wait"/> behind another.</summary>
    private async Task<JsonElement> RunQueuedAsync(TimeSpan wait, bool fail = false)
    {
        var limiter = Limiter(1);
        var held = await limiter.EnterAsync(CancellationToken.None);
        var call = Runner().RunAsync(
            "test_tool",
            async ct =>
            {
                using var slot = await limiter.EnterAsync(ct);
                return fail ? throw new ToolException("Nothing matches.") : ToolResult.Ok("Fine.", null);
            },
            CancellationToken.None);

        _time.Advance(wait);
        held.Dispose();
        return JsonDocument.Parse(await call.WaitAsync(TimeSpan.FromSeconds(10))).RootElement;
    }
}
