using DashyNMS.Mcp.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Mcp.Tests;

/// <summary>What a token may hold open, and what refusals leave in the log (#17). The rest is in <see cref="HttpTransportTests"/>.</summary>
public sealed class BearerGateTests
{
    private readonly ManualTime _time = new();
    private readonly CountingLogger _log = new();

    private BearerGate Gate()
        => new(HttpTokens.Parse($"alice:{HttpMcpServer.AliceToken},bob:{HttpMcpServer.BobToken}", McpOptions.HttpTokensKey, [])!, [], _log, _time);

    private static DefaultHttpContext Request(string? token, string path = "/mcp")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        if (token is not null)
        {
            context.Request.Headers.Authorization = "Bearer " + token;
        }

        return context;
    }

    [Fact]
    public async Task One_token_cannot_hold_every_connection()
    {
        var gate = Gate();
        var hold = new TaskCompletionSource();
        var held = Enumerable.Range(0, BearerGate.MaxOpenRequests).Select(_ => gate.InvokeAsync(Request(HttpMcpServer.AliceToken), _ => hold.Task)).ToArray();

        var over = Request(HttpMcpServer.AliceToken);
        await gate.InvokeAsync(over, _ => throw new InvalidOperationException("It should have been refused."));
        Assert.Equal(StatusCodes.Status429TooManyRequests, over.Response.StatusCode);

        // Somebody else's are their own, and so are the probes.
        var others = 0;
        RequestDelegate other = _ =>
        {
            others++;
            return Task.CompletedTask;
        };
        await gate.InvokeAsync(Request(HttpMcpServer.BobToken), other);
        var probe = Request(token: null, HttpServer.HealthPath);
        probe.Request.Method = HttpMethods.Get;
        await gate.InvokeAsync(probe, other);
        Assert.Equal(2, others);

        hold.SetResult();
        await Task.WhenAll(held);
        await gate.InvokeAsync(Request(HttpMcpServer.AliceToken), other);
        Assert.Equal(3, others);
    }

    [Fact]
    public async Task A_request_that_fails_is_no_longer_open()
    {
        var gate = Gate();
        for (var i = 0; i < BearerGate.MaxOpenRequests * 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.InvokeAsync(Request(HttpMcpServer.AliceToken), _ => throw new InvalidOperationException()));
        }
    }

    [Fact]
    public async Task Refusals_are_logged_once_a_minute_without_what_was_sent()
    {
        var gate = Gate();
        for (var i = 0; i < 100; i++)
        {
            await gate.InvokeAsync(Request("synthetic-guess"), _ => throw new InvalidOperationException("It should have been refused."));
        }

        Assert.Contains("Refused 1 HTTP requests", Assert.Single(_log.Lines), StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromMinutes(1));
        await gate.InvokeAsync(Request(token: null), _ => throw new InvalidOperationException("It should have been refused."));
        Assert.Equal(2, _log.Lines.Count);
        Assert.Contains("Refused 100 HTTP requests", _log.Lines[1], StringComparison.Ordinal);
        Assert.All(_log.Lines, l => Assert.DoesNotContain("synthetic-guess", l, StringComparison.Ordinal));
    }

    private sealed class CountingLogger : ILogger<BearerGate>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
