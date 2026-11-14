using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// "Where is this plugged in?" (#19): from a MAC or IP address to the switch
/// port it's on, with the evidence - the PoC brief's stretch goal.
/// </summary>
/// <remarks>
/// <para>
/// Every switch on the way learns the MAC, so the forwarding tables alone
/// give a list of ports, not an answer. The edge port is the one that isn't
/// a link to another switch: a port with an LLDP or CDP neighbour LibreNMS
/// knows, an aggregate, a VLAN interface or a port holding many MACs is
/// passed over. What's left, fewest MACs first, is the answer - and when
/// more than one port is left, or none, the result says it's ambiguous
/// rather than guessing.
/// </para>
/// <para>
/// LibreNMS looks a MAC up across every device's FDB with
/// <c>resources/fdb/{mac}</c>, which Core doesn't wrap; it only matches the
/// form LibreNMS stores (12 lowercase hex digits, checked live), so that's
/// what goes in the URL.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class TraceTools(
    ILibreNmsClient client,
    ILibreNmsTransport transport,
    FleetCache fleet,
    DeviceDirectory directory,
    ToolRunner runner,
    AgentOptions options)
{
    /// <summary>A port with more MACs than this is carrying other switches' traffic.</summary>
    public const int UplinkMacCount = 10;

    /// <summary>The switches looked at closely (ports and MAC counts), at most. A gateway's MAC can be on every switch.</summary>
    public const int MaxSwitches = 25;

    /// <summary>The MACs traced for one IP, at most: more means an ARP table full of stale or proxy entries.</summary>
    public const int MaxMacsPerIp = 3;

    public const int MaxSeenOn = 50;

    [McpServerTool(Name = ToolText.TraceEndpoint.Name, Title = ToolText.TraceEndpoint.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TraceEndpoint.Description)]
    public Task<string> TraceEndpoint(
        [Description(ToolText.TraceEndpoint.Args.Address)] string address,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TraceEndpoint.Name, ct => TraceAsync(address, ct), cancellationToken);

    private async Task<JsonObject> TraceAsync(string address, CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Ports.TraceEndpoint(client, transport, fleet, directory, options, runner.Redactor)
            .ReadAsync(new(address), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data, result.Omitted);
    }

    internal static string? Mac(string text) => DashyNMS.Agent.Ports.TraceEndpoint.Mac(text);
}
