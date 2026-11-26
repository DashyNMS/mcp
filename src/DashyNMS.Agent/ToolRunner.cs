using System.Text.Json.Nodes;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Requests;
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
    /// <summary>
    /// The shortest wait worth telling the model about. Below it, parallel
    /// calls overlapped briefly, which is what the queue is for; saying so
    /// every time would only teach the model to ignore it.
    /// </summary>
    internal static readonly TimeSpan ReportedWait = TimeSpan.FromSeconds(1);

    internal Redactor Redactor => redactor;

    internal AgentOptions Options => options;

    /// <summary>Sees each result as it's returned, so every tool test also holds it to the tool's output schema (#40).</summary>
    internal Action<string, string>? Returned { get; set; }

    public async Task<string> RunAsync(string tool, Func<CancellationToken, Task<JsonObject>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var waits = ToolCallWaits.Begin();
        var sources = SourceOutcomes.Begin();
        JsonObject result;
        var failed = false;
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
            failed = true;
        }
        catch (Exception ex)
        {
            failed = true;
            sources.Failed(ex);
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

        if (waits.Longest >= ReportedWait)
        {
            result["throttling"] = new JsonObject
            {
                ["waited_seconds"] = Math.Round(waits.Longest.TotalSeconds, 1),
                ["message"] = "Requests for this call queued behind others. Make fewer calls at once.",
            };
        }

        var json = ToolResult.Render(result, redactor, options.MaxResultBytes);
        Returned?.Invoke(tool, json);
        ToolCallReport.Current?.Answer(Outcome(failed, sources), json);
        return json;
    }

    private static ToolCallOutcome Outcome(bool failed, SourceOutcomes sources)
    {
        if (sources.WasRefused)
        {
            return ToolCallOutcome.TokenRefused;
        }

        // An answer without what this account may not see is all of it, for this account.
        if (sources.WasDenied && (failed || !sources.IsComplete))
        {
            return ToolCallOutcome.Denied;
        }

        // A tool turns some sources' failures into its own sentence, so what
        // the sources did counts, not only what it threw.
        if (failed ? sources.AnyUnreachable : sources.IsUnreachable)
        {
            return ToolCallOutcome.Unreachable;
        }

        return failed ? ToolCallOutcome.Failed : sources.IsComplete ? ToolCallOutcome.Complete : ToolCallOutcome.Partial;
    }
}
