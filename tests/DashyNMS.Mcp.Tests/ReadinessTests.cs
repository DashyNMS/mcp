using DashyNMS.Mcp.Http;
using DesktopNMS.Core.Api;

namespace DashyNMS.Mcp.Tests;

/// <summary>The readiness answer (#17): LibreNMS's, kept for a while so a probe can't be used to flood it.</summary>
public sealed class ReadinessTests
{
    private readonly ILibreNmsClient _client = Substitute.For<ILibreNmsClient>();
    private readonly ManualTime _time = new();
    private int _checks;

    private Readiness Create(Func<Task<ConnectionTestResult>> answer)
    {
        _client.TestAsync(Arg.Any<LibreNmsConnection>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref _checks);
            return answer();
        });
        var options = McpOptions.TryLoad(
            key => key switch
            {
                McpOptions.LibreNmsUrl => "https://librenms.example.net",
                McpOptions.LibreNmsToken => "synthetic-token",
                _ => null,
            },
            out _)!;
        return new Readiness(_client, options, _time);
    }

    [Fact]
    public async Task It_follows_LibreNMS_once_the_last_answer_is_old_enough()
    {
        var answer = ConnectionTestResult.Failure("The server didn't answer.");
        using var readiness = Create(() => Task.FromResult(answer));

        Assert.False(await readiness.IsReadyAsync(CancellationToken.None));
        answer = ConnectionTestResult.Success(new DesktopNMS.Core.Models.SystemInfo());
        Assert.False(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(1, _checks);

        _time.Advance(Readiness.Lifetime);
        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(2, _checks);
    }

    [Fact]
    public async Task Everyone_asking_at_once_shares_one_check()
    {
        var pending = new TaskCompletionSource<ConnectionTestResult>();
        using var readiness = Create(() => pending.Task);

        var asking = Enumerable.Range(0, 50).Select(_ => readiness.IsReadyAsync(CancellationToken.None)).ToArray();
        pending.SetResult(ConnectionTestResult.Success(new DesktopNMS.Core.Models.SystemInfo()));

        Assert.All(await Task.WhenAll(asking), ready => Assert.True(ready));
        Assert.Equal(1, _checks);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
