using System.Net;
using System.Runtime.CompilerServices;
using DesktopNMS.Core.Api;

namespace DashyNMS.Agent;

/// <summary>
/// How the sources one tool call read from answered it (#39): an operation
/// that gives what it could read, and says what it couldn't, is worth
/// remembering only if nothing was missing.
/// </summary>
/// <remarks>
/// Ambient for the same reason as the adapter's request waits: the reads are
/// several layers below the tool, and only the adapter wants to know. Hosts
/// that don't begin one record nothing.
/// </remarks>
internal sealed class SourceOutcomes
{
    private static readonly AsyncLocal<SourceOutcomes?> Ambient = new();
    private static readonly AsyncLocal<StrongBox<bool>?> CachedRead = new();

    private int _answered;
    private int _unreachable;
    private int _denied;
    private int _refused;
    private int _partial;

    public static SourceOutcomes? Current => Ambient.Value;

    /// <summary>Starts recording for a call. Call it from the async method the call runs in, which is as far as it reaches.</summary>
    public static SourceOutcomes Begin()
    {
        var outcomes = new SourceOutcomes();
        Ambient.Value = outcomes;
        return outcomes;
    }

    /// <summary>Nothing this account may see is missing from the answer.</summary>
    public bool IsComplete => _unreachable == 0 && _refused == 0 && _partial == 0;

    /// <summary>A source couldn't be reached, and none answered - a list fetched a moment before isn't an answer.</summary>
    public bool IsUnreachable => _unreachable > 0 && _answered == 0;

    /// <summary>A source couldn't be reached, whatever the others did.</summary>
    public bool AnyUnreachable => _unreachable > 0;

    /// <summary>A source wouldn't give this account something.</summary>
    public bool WasDenied => _denied > 0;

    /// <summary>LibreNMS refused the token itself.</summary>
    public bool WasRefused => _refused > 0;

    /// <summary>
    /// Marks the start of one read, so <see cref="Answered"/> knows whether it
    /// came from a list already held. Call it from the async method the read runs in.
    /// </summary>
    public static StrongBox<bool> BeginRead() => CachedRead.Value = new StrongBox<bool>();

    /// <summary>The read in progress, if any, is being given a list already held.</summary>
    public static void NoteCached()
    {
        if (CachedRead.Value is { } read)
        {
            read.Value = true;
        }
    }

    public void Answered(StrongBox<bool> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!read.Value)
        {
            Interlocked.Increment(ref _answered);
        }
    }

    /// <summary>The answer leaves something out for a reason that isn't a failed read.</summary>
    public void Partial() => Interlocked.Increment(ref _partial);

    public void Failed(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is LibreNmsApiException { IsAuthenticationFailure: true })
        {
            Interlocked.Increment(ref _refused);
        }
        else if (error is LibreNmsApiException { IsPermissionDenied: true } or LibreNmsApiException { IsAdminOnlyInApi: true } or Graphs.GraphDeniedException)
        {
            Interlocked.Increment(ref _denied);
        }
        else if (IsUnreachableError(error))
        {
            Interlocked.Increment(ref _unreachable);
        }
        else
        {
            Interlocked.Increment(ref _partial);
        }
    }

    /// <summary>LibreNMS couldn't be reached, took too long, or had trouble of its own - as opposed to answering that it won't or can't.</summary>
    private static bool IsUnreachableError(Exception error) => error switch
    {
        LibreNmsApiException api => api.IsTimeout || api.StatusCode is null or HttpStatusCode.TooManyRequests || (int)api.StatusCode >= 500,
        OperationCanceledException or TimeoutException or HttpRequestException => true,
        _ => false,
    };
}
