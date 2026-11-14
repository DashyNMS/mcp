using DesktopNMS.Core;

namespace DashyNMS.Agent;

/// <summary>A source's answer, or why there isn't one.</summary>
public sealed record Fetched<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Where a part of a result couldn't be fetched; the rest of the result still stands.</summary>
public sealed record Unavailable(string Source, string Message)
{
    /// <summary>
    /// "denied" when LibreNMS refused this account (on 26.10 it checks reads per
    /// device and resource), otherwise "failed" - so an answer never takes a
    /// part it wasn't allowed to see, or couldn't load, as empty (#79).
    /// </summary>
    public string Reason => Message.Contains(DesktopNMS.Core.Api.LibreNmsApiException.PermissionDeniedMessage, StringComparison.Ordinal)
        || Message.Contains("isn't allowed", StringComparison.Ordinal)
        ? "denied"
        : "failed";
}

/// <summary>Helpers for tools that combine several calls.</summary>
public static class Sources
{
    /// <summary>
    /// Runs one call of several, so a single failure (a 403 on one endpoint
    /// for a read-only user, a sensor that times out) costs only its part of
    /// the answer. Bugs and the caller's cancellation still propagate.
    /// </summary>
    public static async Task<Fetched<T>> TryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return new Fetched<T>(await call().ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ToolErrors.MessageFor(ex) is not null)
        {
            return new Fetched<T>(default, ToolErrors.MessageFor(ex));
        }
    }

    /// <summary>
    /// A LibreNMS timestamp in UTC. Zone-less ones are UTC or the server's
    /// local time per <see cref="AgentOptions.ServerTimestampsAreUtc"/>, the
    /// same rule desktop applies (see Core's <see cref="ServerTime"/>).
    /// </summary>
    public static DateTime? Utc(DateTime? value, AgentOptions options)
        => value is { } v ? ServerTime.ToLocal(v, options.ServerTimestampsAreUtc).ToUniversalTime() : null;
}
