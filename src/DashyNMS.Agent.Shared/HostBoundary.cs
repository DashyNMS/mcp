using DesktopNMS.Core.Api;

namespace DashyNMS.Agent;

/// <summary>
/// Stable host-facing failure categories for typed host operations. No server text or exception
/// details travel with them.
/// </summary>
public enum HostFailure
{
    InvalidRequest,
    Refused,
    NotFound,
    Timeout,
    Unavailable,
    Failed,
}

/// <summary>
/// The one safe boundary every host operation runs through, so device search, open alerts, device
/// summaries and later operations classify failures and handle cancellation identically.
/// </summary>
internal static class HostBoundary
{
    /// <summary>
    /// Runs one operation for a host. Returns its result, or a fixed category without logging.
    /// Caller and retired-generation cancellation throw. WaitAsync bounds the wait when a transport
    /// ignores cancellation; late results are discarded and late faults observed.
    /// </summary>
    internal static async Task<(T? Result, HostFailure? Failure)> RunAsync<T>(
        Func<CancellationToken, Task<T>> run, CancellationToken cancellationToken, CancellationToken lifetime)
        where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        Task<T>? inner = null;
        try
        {
            inner = run(token);
            var result = await inner.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return (result, null);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            if (inner is not null)
                _ = ObserveLateFault(inner);
            // Cancellation wins even if an uncooperative transport completes with a failure.
            token.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception error)
        {
            return (null, Classify(error));
        }
    }

    internal static HostFailure Classify(Exception error) => error switch
    {
        ToolException => HostFailure.InvalidRequest,
        Devices.DeviceNotFoundException => HostFailure.NotFound,
        LibreNmsApiException api when api.IsAuthenticationFailure || api.IsPermissionDenied || api.IsAdminOnlyInApi => HostFailure.Refused,
        LibreNmsApiException { StatusCode: System.Net.HttpStatusCode.NotFound } => HostFailure.NotFound,
        LibreNmsApiException { IsTimeout: true } => HostFailure.Timeout,
        OperationCanceledException or TimeoutException => HostFailure.Timeout,
        LibreNmsApiException or HttpRequestException => HostFailure.Unavailable,
        _ => HostFailure.Failed,
    };

    // WaitAsync can abandon an executor whose transport ignores cancellation. Reading
    // Exception observes its eventual failure without logging or retaining server details.
    internal static Task ObserveLateFault(Task task) => task.ContinueWith(
        static completed => { _ = completed.Exception; },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
