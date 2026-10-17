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

    public static string StatusOf(Device device) => device switch
    {
        { Disabled: true } => "disabled",
        { Status: true } => "up",
        _ => "down",
    };
}
