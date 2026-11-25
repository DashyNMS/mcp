using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Requests;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DashyNMS.Mcp;

/// <summary>Answers a call from the last good answer to the same question when LibreNMS can't be reached (#39).</summary>
public sealed class LastKnownGoodFilter(LastKnownGood answers, Redactor redactor, int maxResultBytes)
{
    /// <summary>
    /// Tools whose answers aren't kept. Config and log text is the likeliest to
    /// hold a secret the redactor doesn't know, a graph is an image, and help never needs LibreNMS.
    /// </summary>
    public static readonly IReadOnlySet<string> NotKept = new HashSet<string>(StringComparer.Ordinal)
    {
        ToolText.ConfigBackup.Name,
        ToolText.ConfigDiff.Name,
        ToolText.RecentConfigChanges.Name,
        ToolText.WhatChanged.Name,
        ToolText.SearchLogs.Name,
        ToolText.Graph.Name,
        ToolText.DashynmsHelp.Name,
    };

    /// <summary>How <see cref="ToolResult"/> starts every answer.</summary>
    private const string OkStart = "{\"status\":\"ok\"";

    public async ValueTask<CallToolResult> RunAsync(
        RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.MatchedPrimitive is not McpServerTool { ProtocolTool: { } tool })
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }

        // For every tool, kept or not: a token refused to one is refused to all.
        var generation = answers.Generation;
        var report = ToolCallReport.Begin();
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        var question = Question(tool, context.Params?.Arguments);
        switch (report.Outcome)
        {
            // Never an answer anything was redacted from: what's kept has nothing to hide.
            // Nor one too big to return, which the tool answered and the cap made an error.
            case ToolCallOutcome.Complete when !NotKept.Contains(tool.Name) && report.Json is { } json && json.StartsWith(OkStart, StringComparison.Ordinal) && !json.Contains(Redactor.Marker, StringComparison.Ordinal):
                answers.Keep(question, json, generation);
                break;

            case ToolCallOutcome.TokenRefused:
                answers.Clear();
                break;

            // LibreNMS answered, and the answer is now that there's none: no such
            // device, perhaps because this account no longer sees it.
            case ToolCallOutcome.Denied or ToolCallOutcome.Failed:
                answers.Forget(question);
                break;

            case ToolCallOutcome.Unreachable when answers.Find(question) is { } known:
                return new CallToolResult { Content = [new TextContentBlock { Text = Stale(known, report.Json) }] };
        }

        return result;
    }

    /// <summary>The call as one line: the tool and the arguments that make a difference, in an order of their own.</summary>
    internal static string Question(Tool tool, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var line = new StringBuilder(tool.Name);
        if (arguments is not null && tool.InputSchema.TryGetProperty("properties", out var declared))
        {
            foreach (var (name, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                // Left out and given as its default are the same question.
                if (declared.TryGetProperty(name, out var schema)
                    && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && !(schema.TryGetProperty("default", out var usual) && JsonElement.DeepEquals(usual, value)))
                {
                    line.Append('\n').Append(name).Append('=').Append(value.GetRawText());
                }
            }
        }

        return line.ToString();
    }

    private string Stale(KnownAnswer known, string? failure)
    {
        var asOf = known.AsOf.UtcDateTime;
        var result = JsonNode.Parse(known.Json)!.AsObject();
        var summary = result["summary"]?.GetValue<string>();
        var answer = new JsonObject
        {
            ["status"] = "ok",
            ["summary"] = $"LibreNMS can't be reached, so this is the last known answer, from {asOf.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC. {summary}",
            ["stale"] = true,
            ["as_of"] = asOf.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["stale_reason"] = Reason(failure),
        };
        foreach (var (name, value) in result)
        {
            // How long that call's requests queued says nothing about this one.
            if (!answer.ContainsKey(name) && name != "throttling")
            {
                answer[name] = value?.DeepClone();
            }
        }

        return ToolResult.Render(answer, redactor, maxResultBytes);
    }

    /// <summary>The failure's own sentence, or ours for an answer that only lacked every part of itself.</summary>
    private static string Reason(string? failure)
    {
        if (failure is not null)
        {
            using var json = JsonDocument.Parse(failure);
            if (json.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return "LibreNMS didn't answer.";
    }
}

public static class LastKnownGoodExtensions
{
    /// <summary>Inside the rate limit and the audit, so a stale answer counts and is recorded like any other.</summary>
    public static IMcpServerBuilder WithLastKnownGood(this IMcpServerBuilder builder, Func<IServiceProvider, LastKnownGoodFilter> create)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(create);
        builder.Services.AddSingleton(create);
        builder.Services.AddOptions<McpServerOptions>().Configure<LastKnownGoodFilter>((options, filter) =>
            options.Filters.Request.CallToolFilters.Add(
                next => (context, cancellationToken) => filter.RunAsync(context, next, cancellationToken)));
        return builder;
    }
}
