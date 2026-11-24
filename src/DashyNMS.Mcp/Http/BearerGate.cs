using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Mcp.Http;

/// <summary>
/// Lets a request through only with a valid token (#17). Everything needs one
/// except the two probes, unknown paths included, so nothing is ever exposed
/// by forgetting to protect it.
/// </summary>
internal sealed class BearerGate(HttpTokens tokens, IReadOnlyCollection<string> allowedOrigins, ILogger<BearerGate> logger, TimeProvider time)
{
    /// <summary>
    /// The most requests one token may have open. Clients keep a stream open
    /// per session as well as their calls, so it's generous; it's there so one
    /// client gone wrong can't take every connection, the probes' included.
    /// </summary>
    internal const int MaxOpenRequests = 64;

    private const string Scheme = "Bearer ";
    private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, StrongBox<int>> _open = new(StringComparer.Ordinal);
    private readonly object _refusals = new();
    private int _refused;
    private long? _refusedLogged;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (IsProbe(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (tokens.LabelFor(TokenOf(context.Request)) is not { } label)
        {
            LogRefusal();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"dashynms\"";
            return;
        }

        // Web pages send Origin. None has any business here, whatever address it
        // found the server at. The exceptions are for clients that aren't pages
        // but send one all the same.
        var origins = context.Request.Headers.Origin;
        if (origins.Count > 0 && !(origins.Count == 1 && allowedOrigins.Contains(origins[0]!, StringComparer.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var open = _open.GetOrAdd(label, _ => new StrongBox<int>());
        try
        {
            if (Interlocked.Increment(ref open.Value) > MaxOpenRequests)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "1";
                return;
            }

            // The label is who this is from here on: the MCP session belongs to it,
            // its calls are counted against it and the audit trail names it.
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, label)], authenticationType: "Bearer"));
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref open.Value);
        }
    }

    /// <summary>
    /// Says that requests are being refused, at most once a minute and with
    /// nothing from them, so guessing shows in the log and can't fill it.
    /// </summary>
    private void LogRefusal()
    {
        int count;
        lock (_refusals)
        {
            _refused++;
            if (_refusedLogged is { } logged && time.GetElapsedTime(logged) < RefusalLogInterval)
            {
                return;
            }

            count = _refused;
            _refused = 0;
            _refusedLogged = time.GetTimestamp();
        }

        logger.LogWarning("Refused {Count} HTTP requests without a valid token since the last time this was logged", count);
    }

    private static bool IsProbe(HttpRequest request)
        => (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && (request.Path == HttpServer.HealthPath || request.Path == HttpServer.ReadyPath);

    private static string? TokenOf(HttpRequest request)
        => request.Headers.Authorization is [{ } header] && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..].Trim()
            : null;
}
