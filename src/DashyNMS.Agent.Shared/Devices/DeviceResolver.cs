using System.Globalization;
using System.Net;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>How a device phrase resolved.</summary>
public enum ResolvedMatch
{
    /// <summary>Exactly one device: <see cref="DeviceResolution.Device"/>.</summary>
    Found,

    /// <summary>Several devices in the first tier that matched anything: <see cref="DeviceResolution.Candidates"/>. Never a guess.</summary>
    Ambiguous,

    /// <summary>No device visible to the account matches.</summary>
    NoMatch,
}

/// <summary>Just enough to tell candidates apart, redacted: the short form MCP lists for an ambiguous name.</summary>
public sealed record DeviceCandidate(int DeviceId, string? Hostname, string? Display, string? Ip);

/// <summary>A device phrase resolved against the device list.</summary>
/// <param name="Match">Which of the fixed outcomes this is.</param>
/// <param name="Device">
/// The device, redacted and with its reported state, when <see cref="ResolvedMatch.Found"/>. It comes from the
/// device list, which is cached for up to a minute, so its state may be that old: read the device itself
/// (<see cref="HostDeviceSummary"/>) before reporting whether it is up now.
/// </param>
/// <param name="Candidates">Up to <see cref="DeviceResolver.MaxCandidates"/> when <see cref="ResolvedMatch.Ambiguous"/>, in LibreNMS's list order; otherwise empty.</param>
/// <param name="Omitted">Further matching devices not listed in <see cref="Candidates"/>.</param>
public sealed record DeviceResolution(ResolvedMatch Match, DeviceSummary? Device, IReadOnlyList<DeviceCandidate> Candidates, int Omitted);

/// <summary>
/// Turns the device a model names - an ID, hostname, display name, sysName
/// or IP address - into exactly one LibreNMS device, the same way for every
/// tool and every host.
/// </summary>
/// <remarks>
/// <para>
/// Tries the strictest match first and stops at the first tier that finds
/// anything: ID, then an exact name or address, then the short hostname
/// (<c>core-sw1</c> for <c>core-sw1.example.com</c>), then part of a name.
/// Several matches in a tier is ambiguous, with the candidates listed, so the
/// model asks which one rather than guessing.
/// </para>
/// <para>
/// MCP's tools call <see cref="ResolveAsync"/>, which throws a
/// <see cref="ToolException"/> with the sentence the model reads. In-process hosts
/// such as DashyNMS mobile use <see cref="HostDeviceResolver"/>, which returns the
/// same tiers as fixed <see cref="ResolvedMatch"/> outcomes, so a phone can take a
/// device name in one lookup instead of spending one on <c>find_device</c> first
/// (DashyNMS/mcp#131).
/// </para>
/// </remarks>
public sealed class DeviceResolver(DeviceDirectory directory)
{
    public const int MaxCandidates = 5;

    internal const string MissingMessage = "Say which device: a hostname, display name, IP address or LibreNMS device ID.";
    internal const string NoMatchMessage = "No device visible to this account matches. Check the name or address, or search with find_device.";

    /// <summary>The one device the phrase names, or a <see cref="ToolException"/> saying why not, as MCP's tools report it.</summary>
    public async Task<Device> ResolveAsync(string? query, CancellationToken cancellationToken)
    {
        var (device, matches) = await MatchAsync(query, cancellationToken).ConfigureAwait(false);
        if (device is not null)
        {
            return device;
        }

        if (matches.Count > 1)
        {
            throw new ToolException(
                $"{matches.Count} devices match. Ask which one is meant, or use its device ID.",
                new { candidates = matches.Take(MaxCandidates).Select(Candidate.From).ToList(), omitted = Math.Max(0, matches.Count - MaxCandidates) });
        }

        throw new ToolException(NoMatchMessage);
    }

    /// <summary>
    /// The phrase as a fixed outcome, with every device text redacted. An empty phrase is a
    /// <see cref="ToolException"/>, as a request that can't be resolved at all.
    /// </summary>
    public async Task<DeviceResolution> FindAsync(string? query, AgentOptions options, Redactor redactor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);
        var (device, matches) = await MatchAsync(query, cancellationToken).ConfigureAwait(false);
        if (device is not null)
        {
            return new(ResolvedMatch.Found, DeviceSummary.Redacted(device, options, redactor), [], 0);
        }

        if (matches.Count == 0)
        {
            return new(ResolvedMatch.NoMatch, null, [], 0);
        }

        string? Clean(string? text) => text is null ? null : redactor.Redact(text);
        var candidates = matches.Take(MaxCandidates)
            .Select(d => new DeviceCandidate(d.DeviceId, Clean(d.Hostname), Clean(d.Display), Clean(d.Ip)))
            .ToList();
        return new(ResolvedMatch.Ambiguous, null, candidates, matches.Count - candidates.Count);
    }

    /// <summary>
    /// The one device, or every device in the first tier that matched more than one (empty when
    /// nothing matched). Raw devices: MCP's tools redact when they render, so candidates are
    /// listed exactly as they always were.
    /// </summary>
    private async Task<(Device? Device, IReadOnlyList<Device> Matches)> MatchAsync(string? query, CancellationToken cancellationToken)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ToolException(MissingMessage);
        }

        var devices = await directory.GetAllAsync(cancellationToken).ConfigureAwait(false);

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && devices.FirstOrDefault(d => d.DeviceId == id) is { } byId)
        {
            return (byId, [byId]);
        }

        var address = IPAddress.TryParse(text, out var parsed) ? parsed : null;

        Func<Device, bool>[] tiers =
        [
            d => Same(d.Hostname, text) || Same(d.SysName, text) || Same(d.Display, text) || SameAddress(d.Ip, address, text),
            d => Same(ShortName(d.Hostname), text) || Same(ShortName(d.SysName), text),
            d => Contains(d.Hostname, text) || Contains(d.SysName, text) || Contains(d.Display, text),
        ];

        foreach (var tier in tiers)
        {
            var matches = devices.Where(tier).ToList();
            if (matches.Count == 1)
            {
                return (matches[0], matches);
            }

            if (matches.Count > 1)
            {
                return (null, matches);
            }
        }

        return (null, []);
    }

    private static bool Same(string? value, string text)
        => value is not null && string.Equals(value.Trim(), text, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string text)
        => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool SameAddress(string? value, IPAddress? address, string text)
        => address is not null && value is not null && IPAddress.TryParse(value, out var ip) ? ip.Equals(address) : Same(value, text);

    private static string? ShortName(string? name)
        => name is not null && name.IndexOf('.', StringComparison.Ordinal) is > 0 and var dot && !IPAddress.TryParse(name, out _) ? name[..dot] : null;

    /// <summary>
    /// Just enough to tell candidates apart; device text, so it goes under details. MCP's own JSON shape,
    /// unredacted until the tool renders: hosts get <see cref="DeviceCandidate"/>, redacted here instead.
    /// </summary>
    public sealed record Candidate(int DeviceId, string? Hostname, string? Display, string? Ip)
    {
        public static Candidate From(Device device) => new(device.DeviceId, device.Hostname, device.Display, device.Ip);
    }
}
