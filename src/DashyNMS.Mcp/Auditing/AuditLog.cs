using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace DashyNMS.Mcp.Auditing;

/// <summary>Somewhere audit entries go. It must not throw or block: recording a call never gets in its way.</summary>
public interface IAuditSink
{
    void Write(AuditEntry entry);
}

/// <summary>
/// Audit entries as JSON lines on stderr, beside the server's other logs.
/// </summary>
/// <remarks>
/// <para>
/// Written directly rather than through the logging framework, whose level
/// filters could drop an entry. stdout isn't an option, as it carries the MCP
/// protocol over stdio.
/// </para>
/// <para>
/// From a thread of its own, because writing can block: over stdio stderr is a
/// pipe, and a client that doesn't read it would otherwise stop every tool call
/// once the pipe was full. Entries wait in a bounded queue. When even that is
/// full they're lost, and the next one that gets through is preceded by a line
/// saying how many.
/// </para>
/// </remarks>
public sealed class StderrAuditSink : IHostedService, IAuditSink
{
    internal const int QueueLength = 1000;

    /// <summary>How long entries still queued at shutdown are given to go.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly TextWriter _writer;
    private readonly Channel<AuditEntry> _queue;
    private long _lost;
    private Task? _writing;

    public StderrAuditSink()
        : this(Console.Error)
    {
    }

    internal StderrAuditSink(TextWriter writer)
    {
        _writer = writer;
        _queue = Channel.CreateBounded<AuditEntry>(
            new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => Interlocked.Increment(ref _lost));
    }

    public void Write(AuditEntry entry) => _queue.Writer.TryWrite(entry);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A background thread, so one stuck on a full pipe can't keep the process alive.
        _writing = Task.Factory.StartNew(WriteAll, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (_writing is not null)
        {
            // Not the host's token, which may be spent by now: these are the last calls' entries.
            await Task.WhenAny(_writing, Task.Delay(FlushTimeout, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private void WriteAll()
    {
        while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_queue.Reader.TryRead(out var entry))
            {
                WriteLost();
                WriteLine(entry.ToJson());
            }
        }

        WriteLost();
    }

    private void WriteLost()
    {
        if (Interlocked.Exchange(ref _lost, 0) is var lost and > 0)
        {
            WriteLine(new JsonObject
            {
                ["event"] = "audit_lost",
                ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["count"] = lost,
            }.ToJsonString());
        }
    }

    private void WriteLine(string line)
    {
        try
        {
            _writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nobody is reading stderr any more. There's nowhere left to say so.
        }
    }
}
