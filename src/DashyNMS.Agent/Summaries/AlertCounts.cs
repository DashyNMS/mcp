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

/// <summary>
/// What each alert log row means for its alert (#77), so a fault coming or
/// going isn't read as the whole alert starting or recovering: on LibreNMS
/// 26.10 one core recovering is <c>fault_cleared</c> while others are still
/// open, and only the last one clearing is <c>recovered</c>.
/// </summary>
public static class AlertLogKinds
{
    /// <summary>Each entry's kind: active, fault_added, fault_cleared, recovered, acknowledged, worse, better or changed - the alert's own state words, plus the two for one fault of several.</summary>
    public static IReadOnlyDictionary<AlertLogEntry, string> Describe(IEnumerable<AlertLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var kinds = new Dictionary<AlertLogEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var rule in entries.Where(e => e.TimeLogged is not null).GroupBy(e => (e.DeviceId, e.RuleId)))
        {
            var open = new HashSet<int>();
            foreach (var entry in rule.OrderBy(e => e.TimeLogged).ThenBy(e => e.Id))
            {
                kinds[entry] = KindOf(entry, open);
            }
        }

        return kinds;
    }

    private static string KindOf(AlertLogEntry entry, HashSet<int> open)
    {
        if (entry.FaultId is not { } fault)
        {
            // A legacy alert's own rows, or a 26.10 rule-level worse/better/changed row
            // (state 5, which Core's AlertState doesn't name and would read as active).
            return entry.StateValue switch
            {
                (int)AlertState.Active => "active",
                (int)AlertState.Recovered => "recovered",
                (int)AlertState.Acknowledged => "acknowledged",
                (int)AlertState.Worse => "worse",
                (int)AlertState.Better => "better",
                5 => "changed",
                _ => entry.State.ToString().ToLowerInvariant(),
            };
        }

        switch (entry.StateValue)
        {
            case (int)AlertState.Active:
                var first = open.Count == 0;
                open.Add(fault);
                return first ? "active" : "fault_added";

            case (int)AlertState.Recovered:
                // Only a fault seen opening, clearing the last one, is known to end the alert.
                return open.Remove(fault) && open.Count == 0 ? "recovered" : "fault_cleared";

            default:
                return entry.State.ToString().ToLowerInvariant();
        }
    }
}
