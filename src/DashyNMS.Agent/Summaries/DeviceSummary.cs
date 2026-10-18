using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>Who a device is, compactly - the shape every tool uses to name one.</summary>
public sealed record DeviceSummary(
    int DeviceId,
    string? Hostname,
    string? Display,
    string? SysName,
    string? Ip,
    string? Os,
    string? Hardware,
    string? Location,
    string Status,
    DateTime? LastPolled)
{
    public static DeviceSummary From(Device device, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceSummary(
            device.DeviceId,
            device.Hostname,
            // LibreNMS fills display with the hostname when no display name is set; repeating it adds nothing.
            string.Equals(device.Display, device.Hostname, StringComparison.OrdinalIgnoreCase) ? null : device.Display,
            device.SysName,
            device.Ip,
            device.Os,
            device.Hardware,
            LocationName(device.Location),
            StatusOf(device),
            Sources.Utc(device.LastPolled, options));
    }

    /// <summary>Full when asked for, otherwise the few fields that tell devices apart (#35).</summary>
    public static object Shape(Device device, AgentOptions options, bool verbose)
        => verbose ? From(device, options) : DeviceBrief.From(device);

    /// <summary>
    /// A device's location as its name. The device list gives the name, but
    /// devices/{id} gives the whole location record as JSON ({"id": 29,
    /// "location": "...", "lat": ...}) and Core keeps it as text (found in
    /// #13's trial), so the name is read out of that.
    /// </summary>
    public static string? LocationName(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var text = location.Trim();
        if (!text.StartsWith('{'))
        {
            return text;
        }

        try
        {
            using var record = System.Text.Json.JsonDocument.Parse(text);
            return record.RootElement.TryGetProperty("location", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()!.Trim()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }

    /// <summary>
    /// The display name when it says more than the hostname. Many networks
    /// add devices to LibreNMS by IP, so the hostname is an address and the
    /// display name is the only readable one (found in #13's trial).
    /// </summary>
    public static string? DisplayOf(Device? device)
        => device is not null && !string.IsNullOrWhiteSpace(device.Display) && !string.Equals(device.Display, device.Hostname, StringComparison.OrdinalIgnoreCase)
            ? device.Display
            : null;

    /// <summary>The name a person would use: the display name, or the hostname.</summary>
    public static string? NameOf(Device? device) => DisplayOf(device) ?? device?.Hostname;

    public static string StatusOf(Device device) => device switch
    {
        { Disabled: true } => "disabled",
        { Status: true } => "up",
        _ => "down",
    };
}

/// <summary>
/// The short form of a device for long lists (#35): enough to tell devices
/// apart and pick one, at a fraction of the full record's size. Tools return
/// <see cref="DeviceSummary"/> instead when asked to be verbose.
/// </summary>
public sealed record DeviceBrief(int DeviceId, string? Hostname, string? Display, string? Ip, string? Location, string Status)
{
    public static DeviceBrief From(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceBrief(
            device.DeviceId,
            device.Hostname,
            string.Equals(device.Display, device.Hostname, StringComparison.OrdinalIgnoreCase) ? null : device.Display,
            string.Equals(device.Ip, device.Hostname, StringComparison.OrdinalIgnoreCase) ? null : device.Ip,
            DeviceSummary.LocationName(device.Location),
            DeviceSummary.StatusOf(device));
    }
}
