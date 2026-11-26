using System.Text;
using System.Text.Json;
using DashyNMS.Mcp.Auditing;

namespace DashyNMS.Mcp.Tests;

/// <summary>A stderr nobody reads must never hold up a call (#18), as a client needn't read it.</summary>
public sealed class StderrAuditSinkTests
{
    private static AuditEntry Entry(int id) => new(
        DateTimeOffset.UnixEpoch, "stdio", null, null, null, "tool_" + id, null, 0, AuditStatus.Ok, 2, TimeSpan.Zero);

    [Fact]
    public async Task Entries_are_all_written_by_the_time_it_stops()
    {
        var writer = new StuckWriter();
        writer.Release();
        var sink = new StderrAuditSink(writer);
        await sink.StartAsync(CancellationToken.None);
        for (var i = 0; i < 200; i++)
        {
            sink.Write(Entry(i));
        }

        await sink.StopAsync(CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 200).Select(i => "tool_" + i), writer.Lines.Select(l => l.GetProperty("tool").GetString()!));
    }

    [Fact]
    public async Task A_full_pipe_blocks_nothing_and_what_was_lost_is_counted()
    {
        var writer = new StuckWriter();
        var sink = new StderrAuditSink(writer);
        await sink.StartAsync(CancellationToken.None);
        sink.Write(Entry(0));
        await writer.Stuck.WaitAsync(TimeSpan.FromSeconds(30));

        // One is stuck part way through being written, so the queue has room for this many more.
        var writing = Task.Run(() =>
        {
            for (var i = 1; i <= StderrAuditSink.QueueLength + 5; i++)
            {
                sink.Write(Entry(i));
            }
        });
        await writing.WaitAsync(TimeSpan.FromSeconds(30));

        writer.Release();
        await sink.StopAsync(CancellationToken.None);

        Assert.Equal(StderrAuditSink.QueueLength + 1, writer.Lines.Count(l => l.GetProperty("event").GetString() == "tool_call"));
        var lost = Assert.Single(writer.Lines, l => l.GetProperty("event").GetString() == "audit_lost");
        Assert.Equal(5, lost.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Stopping_does_not_wait_for_a_pipe_that_stays_full()
    {
        var writer = new StuckWriter();
        var sink = new StderrAuditSink(writer);
        await sink.StartAsync(CancellationToken.None);
        sink.Write(Entry(0));
        await writer.Stuck.WaitAsync(TimeSpan.FromSeconds(30));

        await sink.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(writer.Lines);
        writer.Release();
    }

    /// <summary>A pipe that's full until it's released.</summary>
    private sealed class StuckWriter : TextWriter
    {
        private readonly ManualResetEventSlim _room = new();
        private readonly TaskCompletionSource _stuck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<JsonElement> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public Task Stuck => _stuck.Task;

        public IReadOnlyList<JsonElement> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Release() => _room.Set();

        public override void WriteLine(string? value)
        {
            _stuck.TrySetResult();
            _room.Wait();
            lock (_lines)
            {
                _lines.Add(JsonDocument.Parse(value!).RootElement);
            }
        }
    }
}
