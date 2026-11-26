using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Requests;

/// <summary>
/// Keeps the requests in flight to one source (LibreNMS, Unimus or Graylog)
/// to a limit, queueing the rest in the order they came (#38).
/// </summary>
/// <remarks>
/// An agent fires tool calls in parallel and some tools make several requests
/// each, so without this a busy server gets them all at once, slows down and
/// answers with timeouts. Core enters the gate before a request's timeout
/// starts, so queueing here never becomes a timeout of its own. Each source
/// has its own limiter: a slow Graylog mustn't hold up LibreNMS.
/// </remarks>
public sealed class RequestLimiter(int limit, TimeProvider time) : IRequestGate
{
    private readonly SemaphoreSlim _slots = new(limit, limit);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_slots.Wait(0, CancellationToken.None))
        {
            var queued = time.GetTimestamp();
            try
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ToolCallWaits.Current?.Record(time.GetElapsedTime(queued));
            }
        }

        return new Slot(_slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        // Once only: giving a slot back twice would raise the limit for good.
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
