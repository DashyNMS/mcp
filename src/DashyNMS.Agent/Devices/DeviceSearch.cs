using System.Text.RegularExpressions;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>Typed search arguments; execution owns ranking, filtering and paging.</summary>
public sealed record FindDevicesRequest(string? Query = null, string? Status = null, string? Group = null,
    string? Location = null, int Offset = 0, int Limit = 10);

/// <summary>A stored ARP observation, not proof of endpoint ownership.</summary>
public sealed record ArpSighting(string? Ip, string? Mac, int PortId);

public sealed record DeviceMatch(DeviceSummary Device, IReadOnlyList<ArpSighting> SeenAs);

/// <summary>Matches before an adapter's presentation cap; Page reports exact upstream omissions.</summary>
public sealed record FindDevicesResult(IReadOnlyList<DeviceMatch> Matches, PageInfo Page, bool IsMacSearch, string Summary);

/// <summary>
/// Shared search execution without MCP invocation or JSON parsing. Create a new instance and
/// caches for every captured connection; the caller owns transport lifetime and cancellation.
/// This executor does not log. Propagated exceptions may contain sensitive messages or details;
/// hosts must map them to fixed safe statuses before logging or presenting failures.
/// </summary>
public sealed partial class DeviceSearch
{
    private readonly ILibreNmsClient client;
    private readonly DeviceDirectory directory;
    private readonly DeviceScope scopes;
    private readonly AgentOptions options;
    private readonly Redactor redactor;

    /// <summary>Reuses MCP's connection-bound caches; other hosts use the isolated factory.</summary>
    internal DeviceSearch(ILibreNmsClient client, DeviceDirectory directory, DeviceScope scopes,
        AgentOptions options, Redactor redactor)
    {
        this.client = client;
        this.directory = directory;
        this.scopes = scopes;
        this.options = options;
        this.redactor = redactor;
    }

    public const int MaxMatches = 50;

    /// <summary>Creates isolated caches around a host-owned, connection-bound client without registering transports.</summary>
    public static DeviceSearch Create(ILibreNmsClient client, TimeProvider time, AgentOptions options, Redactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        return new(client, directory, new DeviceScope(client, directory, time), options, redactor ?? new Redactor());
    }

    private string? Clean(string? text) => text is null ? null : redactor.Redact(text);

    private DeviceSummary Safe(Device device)
    {
        var d = DeviceSummary.From(device, options);
        return d with { Hostname = Clean(d.Hostname), Display = Clean(d.Display), SysName = Clean(d.SysName),
            Ip = Clean(d.Ip), Os = Clean(d.Os), Hardware = Clean(d.Hardware), Location = Clean(d.Location) };
    }

    public async Task<FindDevicesResult> FindDevicesAsync(FindDevicesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (query, status, group, location, offset, limit) = request;
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var wanted = ParseStatus(status);
        if (text is null && wanted is null && string.IsNullOrWhiteSpace(group) && string.IsNullOrWhiteSpace(location))
        {
            throw new ToolException("Give part of a name, an IP address or a MAC address to search for, or a status, group or location to list.");
        }

        Paging.Check(offset, limit, MaxMatches);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool Wanted(Device d) => scope.Includes(d) && (wanted is null || DeviceSummary.StatusOf(d) == wanted);

        if (text is not null && MacAddress().IsMatch(text))
        {
            return await FindByMacAsync(text, Wanted, offset, limit, cancellationToken).ConfigureAwait(false);
        }

        var devices = await directory.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var ranked = devices
            .Where(Wanted)
            .Select(d => (Device: d, Rank: text is null ? 0 : Rank(d, text)))
            .Where(r => r.Rank >= 0)
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Device.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Device)
            .ToList();

        var matches = Paging.Apply(ranked, offset, limit, out var page)
            .Select(d => new DeviceMatch(Safe(d), [])).ToList();
        var summary = page.Total == 0 ? "No devices visible to this account match." : Paging.Describe(page, "matching device", "matching devices");
        cancellationToken.ThrowIfCancellationRequested();
        return new(matches, page, false, summary);
    }

    private async Task<FindDevicesResult> FindByMacAsync(string mac, Func<Device, bool> wanted, int offset, int limit, CancellationToken cancellationToken)
    {
        var entries = await client.Arp.FindByMacAsync(mac, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);

        var seen = entries
            .GroupBy(e => e.DeviceId)
            .Where(g => devices.TryGetValue(g.Key, out var d) && wanted(d))
            .ToList();

        var matches = Paging.Apply(seen, offset, limit, out var page)
            .Select(g => new DeviceMatch(Safe(devices[g.Key]),
                g.Select(e => (e.Ipv4Address, e.MacAddress, e.PortId)).Distinct()
                    .Select(e => new ArpSighting(Clean(e.Ipv4Address), Clean(e.MacAddress), e.PortId)).ToList()))
            .ToList();
        var summary = page.Total == 0
            ? "No device in LibreNMS has this MAC address in its ARP table."
            : $"{page.Total} device{(page.Total == 1 ? " has" : "s have")} this MAC address in its ARP table." +
              (page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "device", "devices") : string.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        return new(matches, page, true, summary);
    }

    private static string? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "any" or "all" => null,
        "up" => "up",
        "down" => "down",
        "disabled" => "disabled",
        _ => throw new ToolException("Status must be up, down or disabled."),
    };

    /// <summary>0 exact, 1 starts with, 2 contains, -1 no match - over the fields people call a device by.</summary>
    private static int Rank(Device device, string text)
    {
        string?[] fields = [device.Hostname, device.SysName, device.Display, device.Ip];
        if (fields.Any(f => string.Equals(f, text, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (fields.Any(f => f?.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true))
        {
            return 1;
        }

        return fields.Any(f => f?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) ? 2 : -1;
    }

    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$|^(?:[0-9A-Fa-f]{4}\.){2}[0-9A-Fa-f]{4}$|^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacAddress();
}
