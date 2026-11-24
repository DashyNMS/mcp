namespace DashyNMS.Mcp;

/// <summary>
/// At most so many tool calls per client in any minute (#38), so one agent
/// stuck in a loop can't use up what a shared server can do for the others.
/// </summary>
/// <remarks>
/// A sliding minute rather than a refilling bucket: "30 a minute" then means
/// exactly that, with no burst of twice the number across a boundary. A call
/// that's refused doesn't count, so waiting the time it's given always works.
/// Clients are the operator's token labels (or the one stdio client), so the
/// number kept is bounded by the settings.
/// </remarks>
public sealed class ToolCallRateLimiter(int callsPerMinute, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<long>> _calls = new(StringComparer.Ordinal);

    public int CallsPerMinute => callsPerMinute;

    /// <summary>Counts a call for <paramref name="client"/>, or says how long until one would be allowed.</summary>
    public bool TryCall(string client, out TimeSpan retryAfter)
    {
        lock (_sync)
        {
            if (!_calls.TryGetValue(client, out var calls))
            {
                _calls[client] = calls = new Queue<long>();
            }

            var now = time.GetTimestamp();
            while (calls.TryPeek(out var oldest) && time.GetElapsedTime(oldest, now) >= Window)
            {
                calls.Dequeue();
            }

            if (calls.Count < callsPerMinute)
            {
                calls.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = Window - time.GetElapsedTime(calls.Peek(), now);
            return false;
        }
    }
}
