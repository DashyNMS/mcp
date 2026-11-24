using DesktopNMS.Core.Api;

namespace DashyNMS.Mcp.Http;

/// <summary>
/// Whether the server can do its job, which is whether LibreNMS answers it.
/// </summary>
/// <remarks>
/// Anyone who can reach the port can ask, so the answer is kept for a while
/// and one check serves everyone waiting: however often it's asked, LibreNMS
/// hears about it at most once per <see cref="Lifetime"/>. The check is Core's
/// connection test rather than an ordinary request: it doesn't queue behind
/// the tools' own (a busy server is still a ready one) and doesn't retry.
/// </remarks>
internal sealed class Readiness(ILibreNmsClient client, McpOptions options, TimeProvider time) : IDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _checking = new(1, 1);
    private (bool Ready, long At)? _last;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last is { } last && time.GetElapsedTime(last.At) < Lifetime)
            {
                return last.Ready;
            }

            // Not the asker's token: hanging up mustn't turn into an answer the next one gets.
            using var timeout = new CancellationTokenSource(CheckTimeout);
            var result = await client.TestAsync(options.Connection, timeout.Token).ConfigureAwait(false);
            _last = (result.Succeeded, time.GetTimestamp());
            return result.Succeeded;
        }
        finally
        {
            _checking.Release();
        }
    }

    public void Dispose() => _checking.Dispose();
}
