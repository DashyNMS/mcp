using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>
/// Open alerts counted the one way every tool counts them (#77), from Core's
/// group model: an alert is a rule firing on a device, however many faults
/// (cores, ports, sensors) it holds on LibreNMS 26.10, and faults are
/// counted separately.
/// </summary>
/// <param name="AlertGroups">Alerts: rules firing on devices.</param>
/// <param name="Faults">The faults they hold - the same as alerts before 26.10.</param>
/// <param name="Acknowledged">Alerts whose every fault is acknowledged.</param>
/// <param name="PartlyAcknowledged">Alerts with some faults acknowledged and some not.</param>
public sealed record OpenAlertTotals(int AlertGroups, int Faults, int Devices, int Critical, int Warning, int Acknowledged, int PartlyAcknowledged)
{
    public static OpenAlertTotals From(IReadOnlyCollection<AlertGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return new OpenAlertTotals(
            groups.Count,
            groups.Sum(g => g.FaultCount),
            groups.Select(g => g.DeviceId).Distinct().Count(),
            groups.Count(g => g.Severity == AlertSeverity.Critical),
            groups.Count(g => g.Severity == AlertSeverity.Warning),
            groups.Count(g => g.State == AlertState.Acknowledged),
            groups.Count(g => g.IsPartlyAcknowledged));
    }

    /// <summary>Groups <paramref name="open"/> (open alerts or faults) and counts them.</summary>
    public static OpenAlertTotals From(IEnumerable<Alert> open) => From(AlertGrouping.ByDeviceAndRule(open.Where(a => a.State != AlertState.Recovered)));

    /// <summary>"3 open alerts", or "3 open alerts (11 faults)" when an alert holds more than one fault.</summary>
    public string Describe()
        => (AlertGroups == 1 ? "1 open alert" : $"{AlertGroups} open alerts") + (Faults > AlertGroups ? $" ({Faults} faults)" : string.Empty);
}
