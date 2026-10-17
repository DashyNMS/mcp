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
            device.Location,
            StatusOf(device),
            Sources.Utc(device.LastPolled, options));
    }

    /// <summary>Full when asked for, otherwise the few fields that tell devices apart (#35).</summary>
    public static object Shape(Device device, AgentOptions options, bool verbose)
        => verbose ? From(device, options) : DeviceBrief.From(device);

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
            string.IsNullOrWhiteSpace(device.Location) ? null : device.Location,
            DeviceSummary.StatusOf(device));
    }
}
