using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DashyNMS.Mcp.Auditing;

/// <summary>
/// Records one entry for every tool call (#18), however it ends.
/// </summary>
/// <remarks>
/// A request filter, so it also sees the calls no tool ever runs for: a tool
/// that doesn't exist or is off, arguments that don't fit, a client over its
/// rate limit. Register it before any other filter: the first is outermost.
/// A message filter outside that catches the calls the SDK can't even read -
/// no tool name, arguments that aren't an object - which never get that far.
/// </remarks>
public sealed class ToolCallAudit(IReadOnlyList<IAuditSink> sinks, AuditRedactor redactor, string transport, TimeProvider time)
{
    /// <summary>Set on a call's <see cref="MessageContext.Items"/> by the filter that refused it.</summary>
    internal const string RateLimitedItem = "DashyNMS.RateLimited";

    /// <summary>Set on a call's items once it has its entry, for the message filter to see.</summary>
    private const string RecordedItem = "DashyNMS.Audited";

    /// <summary>Client names longer than this are the client's problem, not the log's.</summary>
    private const int MaxName = 100;

    public async ValueTask<CallToolResult> RunAsync(
        RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var arrived = time.GetUtcNow();
        var started = time.GetTimestamp();
        var status = AuditStatus.Failed;
        CallToolResult? result = null;
        try
        {
            result = await next(context, cancellationToken).ConfigureAwait(false);
            status = StatusOf(context, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = AuditStatus.Cancelled;
            throw;
        }
        catch (Exception ex) when (ex is McpException or JsonException or ArgumentException)
        {
            // Before the tool ran: the tools themselves turn every failure into a result.
            status = AuditStatus.Rejected;
            throw;
        }
        finally
        {
            Record(context.Items, () => Entry(context, arrived, time.GetElapsedTime(started), status, result), arrived, context.User);
        }
    }

    /// <summary>Records a tools/call that failed before <see cref="RunAsync"/> saw it, and passes everything else by.</summary>
    public async Task RunMessageAsync(MessageContext context, McpMessageHandler next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.JsonRpcMessage is not JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var arrived = time.GetUtcNow();
        var started = time.GetTimestamp();
        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!context.Items.ContainsKey(RecordedItem))
            {
                var status = cancellationToken.IsCancellationRequested ? AuditStatus.Cancelled : AuditStatus.Rejected;
                Record(context.Items, () => Entry(context, request, arrived, time.GetElapsedTime(started), status), arrived, context.User);
            }
        }
    }

    /// <summary>
    /// Writes the entry <paramref name="create"/> makes. Should what the client
    /// sent defeat that, a bare one is written instead: a call that breaks its
    /// own entry must not go unrecorded, nor fail because of it.
    /// </summary>
    private void Record(IDictionary<string, object?> items, Func<AuditEntry> create, DateTimeOffset arrived, ClaimsPrincipal? user)
    {
        items[RecordedItem] = true;
        AuditEntry entry;
        try
        {
            entry = create();
        }
        catch (Exception)
        {
            entry = new AuditEntry(arrived, transport, LabelOf(user), null, null, string.Empty, null, 0, AuditStatus.Failed, 0, TimeSpan.Zero);
        }

        foreach (var sink in sinks)
        {
            sink.Write(entry);
        }
    }

    private static string? LabelOf(ClaimsPrincipal? user) => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private AuditEntry Entry(MessageContext context, JsonRpcRequest request, DateTimeOffset arrived, TimeSpan duration, string status)
    {
        var client = context.Server.ClientInfo;
        var parameters = request.Params as JsonObject;
        var tool = parameters?["name"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
        return new AuditEntry(
            arrived, transport, LabelOf(context.User), Name(client?.Name), Name(client?.Version), Name(tool) ?? string.Empty,
            null, (parameters?["arguments"] as JsonObject)?.Count ?? 0, status, 0, duration);
    }

    private AuditEntry Entry(RequestContext<CallToolRequestParams> context, DateTimeOffset arrived, TimeSpan duration, string status, CallToolResult? result)
    {
        var client = context.Server.ClientInfo;
        var matched = context.MatchedPrimitive as McpServerTool;
        JsonObject? arguments = null;
        var undeclared = context.Params?.Arguments?.Count ?? 0;
        if (matched is not null)
        {
            (arguments, undeclared) = redactor.Arguments(context.Params?.Arguments, matched.ProtocolTool.InputSchema);
        }

        return new AuditEntry(
            arrived,
            transport,
            LabelOf(context.User),
            Name(client?.Name),
            Name(client?.Version),
            matched?.ProtocolTool.Name ?? Name(context.Params?.Name) ?? string.Empty,
            arguments,
            undeclared,
            status,
            SizeOf(result),
            duration);
    }

    /// <summary>A name the client chose for itself or for a tool: as free as any text it sends.</summary>
    private string? Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = redactor.Text(name);
        return text.Length <= MaxName ? text : $"[{text.Length} characters]";
    }

    private static string StatusOf(RequestContext<CallToolRequestParams> context, CallToolResult result)
    {
        if (context.Items.ContainsKey(RateLimitedItem))
        {
            return AuditStatus.RateLimited;
        }

        // A tool's own failure is a result like any other, with its status inside.
        if (result.IsError == true || result.Content.OfType<TextContentBlock>().FirstOrDefault() is not { } text)
        {
            return AuditStatus.Error;
        }

        try
        {
            using var json = JsonDocument.Parse(text.Text);
            if (!json.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || !status.ValueEquals("ok"))
            {
                return AuditStatus.Error;
            }

            return json.RootElement.TryGetProperty("stale", out var stale) && stale.ValueKind == JsonValueKind.True ? AuditStatus.Stale : AuditStatus.Ok;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return AuditStatus.Error;
        }
    }

    private static long SizeOf(CallToolResult? result) => result?.Content.Sum(block => block switch
    {
        TextContentBlock text => Encoding.UTF8.GetByteCount(text.Text),
        ImageContentBlock image => image.DecodedData.Length,
        _ => 0L,
    }) ?? 0;
}

public static class ToolCallAuditExtensions
{
    /// <summary>Records every tool call. Add it before any other filter, so it sees the calls they refuse.</summary>
    /// <param name="create">Makes the audit, once, from the host's services.</param>
    public static IMcpServerBuilder WithAudit(this IMcpServerBuilder builder, Func<IServiceProvider, ToolCallAudit> create)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(create);
        builder.Services.AddSingleton(create);
        builder.Services.AddOptions<McpServerOptions>().Configure<ToolCallAudit>((options, audit) =>
        {
            options.Filters.Message.IncomingFilters.Add(
                next => (context, cancellationToken) => audit.RunMessageAsync(context, next, cancellationToken));
            options.Filters.Request.CallToolFilters.Add(
                next => (context, cancellationToken) => audit.RunAsync(context, next, cancellationToken));
        });
        return builder;
    }
}
