using System.Text.Json.Nodes;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Agent;

/// <summary>
/// Runs a tool's body and turns whatever happens into a <see cref="ToolResult"/>:
/// redacted, capped, and with failures as one plain sentence.
/// </summary>
/// <remarks>
/// Never a stack trace, a raw HTTP body or a URL with a token: each client's
/// own exception is mapped through its <c>ToUserMessage()</c>, which Core
/// already writes for people, and anything unexpected becomes a generic
/// line with the detail in the server log.
/// </remarks>
public sealed class ToolRunner(Redactor redactor, AgentOptions options, ILogger<ToolRunner> logger)
{
    public async Task<string> RunAsync(string tool, Func<CancellationToken, Task<JsonObject>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonObject result;
        try
        {
            result = await body(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolException expected)
        {
            result = ToolResult.Error(expected.Message, expected.Details);
        }
        catch (Exception ex)
        {
            if (ToolErrors.Describe(ex) is { } error)
            {
                logger.LogWarning(ex, "{Tool}: request failed", tool);
                result = ToolResult.Error(error.Message, error.ServerMessage is { } server ? new { server_message = server } : null);
            }
            else
            {
                logger.LogError(ex, "{Tool} failed unexpectedly", tool);
                result = ToolResult.Error($"Something went wrong running {tool}. The server log has the details.");
            }
        }

        return ToolResult.Render(result, redactor, options.MaxResultBytes);
    }
}

/// <summary>Plain-English messages for the failures a tool expects from its sources.</summary>
/// <param name="Message">Our own sentence - the only text a model should take at face value.</param>
/// <param name="ServerMessage">What the server said itself, if anything: returned under details, as data.</param>
public sealed record ToolError(string Message, string? ServerMessage = null);

/// <remarks>
/// LibreNMS's error text and Unimus's and Graylog's response bodies are
/// written by the server, not by us, and can echo parts of a request back
/// (#21). So a message is always our own sentence for the kind of failure,
/// and the server's words go under <c>details.server_message</c>, where
/// device text lives. Core's own messages - timeouts, an unreachable server,
/// a refused token - are ours already and pass through.
/// </remarks>
public static class ToolErrors
{
    /// <summary>The message for an expected failure, or null for one that's a bug.</summary>
    public static string? MessageFor(Exception ex) => Describe(ex)?.Message;

    public static ToolError? Describe(Exception ex) => ex switch
    {
        ToolException expected => new ToolError(expected.Message),
        LibreNmsApiException libreNms => LibreNms(libreNms),
        UnimusApiException unimus => Other("Unimus", unimus.StatusCode, unimus.IsAuthenticationFailure, unimus.ToUserMessage()),
        GraylogApiException graylog => Other("Graylog", graylog.StatusCode, graylog.IsAuthenticationFailure, graylog.ToUserMessage()),

        // Not the caller's cancellation (that propagates), so a request timed out.
        OperationCanceledException => new ToolError("A request timed out. The server may be busy; try again, or ask about less at once."),
        _ => null,
    };

    private static ToolError LibreNms(LibreNmsApiException ex)
    {
        if (ex.IsAuthenticationFailure)
        {
            return new ToolError("LibreNMS: the API token was refused.");
        }

        if (ex.IsPermissionDenied || ex.IsAdminOnlyInApi)
        {
            return new ToolError($"LibreNMS: {ex.ToUserMessage()}");
        }

        var server = string.IsNullOrWhiteSpace(ex.ServerMessage) ? null : ex.ServerMessage.Trim();
        if (ex.StatusCode is { } status)
        {
            return new ToolError($"LibreNMS: {Status("the request", (int)status)}", server);
        }

        // No HTTP response at all: Core's own description of what went wrong.
        return new ToolError($"LibreNMS: {ex.ToUserMessage()}", server);
    }

    private static ToolError Other(string service, System.Net.HttpStatusCode? status, bool authenticationFailure, string coreMessage)
    {
        if (authenticationFailure)
        {
            return new ToolError($"{service}: the API token or password was refused.");
        }

        // With a status, Core's message carries the response body; without one
        // it's Core describing a timeout or connection failure itself.
        return status is { } code
            ? new ToolError($"{service}: {Status("the request", (int)code)}", coreMessage)
            : new ToolError($"{service}: {coreMessage}");
    }

    private static string Status(string what, int code) => code switch
    {
        404 => $"it doesn't have what was asked for (HTTP 404).",
        400 or 422 => $"it rejected {what} as invalid (HTTP {code}).",
        >= 500 => $"it had an error of its own (HTTP {code}). Try again shortly.",
        _ => $"it returned an error (HTTP {code}).",
    };
}
