using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace DashyNMS.Mcp.Http;

/// <summary>
/// Lets a request through only with a valid token (#17). Everything needs one
/// except the two probes, unknown paths included, so nothing is ever exposed
/// by forgetting to protect it.
/// </summary>
internal sealed class BearerGate(HttpTokens tokens, IReadOnlyCollection<string> allowedOrigins)
{
    private const string Scheme = "Bearer ";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (IsProbe(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (tokens.LabelFor(TokenOf(context.Request)) is not { } label)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"dashynms\"";
            return;
        }

        // A browser sends Origin. No page has any business here unless the
        // operator said so, whatever address it found the server at.
        var origins = context.Request.Headers.Origin;
        if (origins.Count > 0 && !(origins.Count == 1 && allowedOrigins.Contains(origins[0]!, StringComparer.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The label is who this is from here on: the MCP session belongs to it,
        // its calls are counted against it and the audit trail names it.
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, label)], authenticationType: "Bearer"));
        await next(context).ConfigureAwait(false);
    }

    private static bool IsProbe(HttpRequest request)
        => (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && (request.Path == HttpServer.HealthPath || request.Path == HttpServer.ReadyPath);

    private static string? TokenOf(HttpRequest request)
        => request.Headers.Authorization is [{ } header] && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..].Trim()
            : null;
}
