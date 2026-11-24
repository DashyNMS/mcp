namespace DashyNMS.Agent.Requests;

/// <summary>
/// How long the requests of one tool call queued, so its result can tell the
/// model to slow down.
/// </summary>
/// <remarks>
/// Ambient, because a request is made several layers below the tool - through
/// Core's APIs - and none of them should have to pass this along. Waiting for
/// a list another call is already fetching isn't counted: that call is the
/// one that queued.
/// </remarks>
internal sealed class ToolCallWaits
{
    private static readonly AsyncLocal<ToolCallWaits?> Ambient = new();

    private long _longestTicks;

    /// <summary>The tool call this code is running for, if any.</summary>
    public static ToolCallWaits? Current => Ambient.Value;

    /// <summary>
    /// The longest one request waited. Not the total: a call's requests wait
    /// side by side, and adding them up would claim more time than passed.
    /// </summary>
    public TimeSpan Longest => TimeSpan.FromTicks(Interlocked.Read(ref _longestTicks));

    /// <summary>Starts counting for a call. Call it from the async method the call runs in, which is as far as it reaches.</summary>
    public static ToolCallWaits Begin()
    {
        var waits = new ToolCallWaits();
        Ambient.Value = waits;
        return waits;
    }

    public void Record(TimeSpan wait)
    {
        var ticks = wait.Ticks;
        var longest = Interlocked.Read(ref _longestTicks);
        while (ticks > longest)
        {
            var seen = Interlocked.CompareExchange(ref _longestTicks, ticks, longest);
            if (seen == longest)
            {
                return;
            }

            longest = seen;
        }
    }
}
