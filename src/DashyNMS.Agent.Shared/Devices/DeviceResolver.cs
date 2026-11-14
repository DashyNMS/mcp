using System.Globalization;
using System.Net;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>
/// Turns the device a model names - an ID, hostname, display name, sysName
/// or IP address - into exactly one LibreNMS device, the same way for every
/// tool.
/// </summary>
/// <remarks>
/// Tries the strictest match first and stops at the first tier that finds
/// anything: ID, then an exact name or address, then the short hostname
/// (<c>core-sw1</c> for <c>core-sw1.example.com</c>), then part of a name.
/// Several matches in a tier is an error that lists the candidates, so the
/// model asks which one rather than guessing.
/// </remarks>
public sealed class DeviceResolver(DeviceDirectory directory)
{
    public const int MaxCandidates = 5;

    public async Task<Device> ResolveAsync(string? query, CancellationToken cancellationToken)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ToolException("Say which device: a hostname, display name, IP address or LibreNMS device ID.");
        }

        var devices = await directory.GetAllAsync(cancellationToken).ConfigureAwait(false);

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && devices.FirstOrDefault(d => d.DeviceId == id) is { } byId)
        {
            return byId;
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
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new ToolException(
                    $"{matches.Count} devices match. Ask which one is meant, or use its device ID.",
                    new { candidates = matches.Take(MaxCandidates).Select(Candidate.From).ToList(), omitted = Math.Max(0, matches.Count - MaxCandidates) });
            }
        }

        throw new ToolException("No device visible to this account matches. Check the name or address, or search with find_device.");
    }

    private static bool Same(string? value, string text)
        => value is not null && string.Equals(value.Trim(), text, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string text)
        => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool SameAddress(string? value, IPAddress? address, string text)
        => address is not null && value is not null && IPAddress.TryParse(value, out var ip) ? ip.Equals(address) : Same(value, text);

    private static string? ShortName(string? name)
        => name is not null && name.IndexOf('.', StringComparison.Ordinal) is > 0 and var dot && !IPAddress.TryParse(name, out _) ? name[..dot] : null;

    /// <summary>Just enough to tell candidates apart; device text, so it goes under details.</summary>
    public sealed record Candidate(int DeviceId, string? Hostname, string? Display, string? Ip)
    {
        public static Candidate From(Device device) => new(device.DeviceId, device.Hostname, device.Display, device.Ip);
    }
}
