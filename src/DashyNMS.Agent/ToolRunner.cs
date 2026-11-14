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
    internal Redactor Redactor => redactor;

    internal AgentOptions Options => options;

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
