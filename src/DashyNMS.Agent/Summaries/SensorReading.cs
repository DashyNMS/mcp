using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>
/// One sensor's reading, judged the way desktop's Health tab judges it
/// (#24): against the sensor's own LibreNMS limits where it has them, and
/// desktop's default thresholds for dBm, signal, temperature and fans where
/// it doesn't (Core's <see cref="SensorCategoryRegistry"/>).
/// </summary>
/// <param name="State">ok, warning, critical, or unknown (no limits to judge by, or a "nothing plugged in" reading).</param>
public sealed record SensorReading(
    int SensorId,
    string Type,
    string? Description,
    double Value,
    string? Unit,
    string State,
    double? LowCritical,
    double? LowWarning,
    double? HighWarning,
    double? HighCritical)
{
    /// <summary>Desktop's defaults; the server has no settings of its own for these.</summary>
    private static readonly AppSettings Defaults = new();

    /// <summary>Units for the classes LibreNMS reports, so a value reads as "48 °C" rather than "48".</summary>
    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temperature"] = "°C",
        ["fanspeed"] = "RPM",
        ["dbm"] = "dBm",
        ["voltage"] = "V",
        ["current"] = "A",
        ["power"] = "W",
        ["humidity"] = "%",
        ["frequency"] = "Hz",
        ["load"] = "%",
        ["percent"] = "%",
        ["runtime"] = "min",
        ["power_consumed"] = "kWh",
        ["airflow"] = "cfm",
        ["pressure"] = "kPa",
    };

    public static SensorReading From(Sensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        var bounds = new ThresholdBounds(sensor.LimitLow, sensor.LimitLowWarn, sensor.LimitHighWarn, sensor.LimitHigh);
        IThresholdEvaluator evaluator = SensorCategoryRegistry.Resolve(sensor.SensorClass) is { } entry
            ? entry.Thresholds(Defaults, sensor)
            : new HybridThresholdEvaluator(default, bounds, overrideSensorLimitsWithAppThresholds: false);

        var hasLimits = SensorCategoryRegistry.Resolve(sensor.SensorClass) is not null
            || bounds.LowCritical is not null || bounds.LowWarning is not null || bounds.HighWarning is not null || bounds.HighCritical is not null;
        var severity = hasLimits ? evaluator.Evaluate(sensor.Current) : AlertSeverity.Unknown;

        return new SensorReading(
            sensor.SensorId,
            sensor.SensorClass ?? "unknown",
            sensor.Description,
            Math.Round(sensor.Current, 2),
            sensor.SensorClass is { } c && Units.TryGetValue(c, out var unit) ? unit : null,
            severity switch
            {
                AlertSeverity.Critical => "critical",
                AlertSeverity.Warning => "warning",
                AlertSeverity.Ok => "ok",
                _ => "unknown",
            },
            sensor.LimitLow,
            sensor.LimitLowWarn,
            sensor.LimitHighWarn,
            sensor.LimitHigh);
    }

    /// <summary>Critical first, then warning, then the rest - the order a problem list wants.</summary>
    public int Rank => State switch
    {
        "critical" => 0,
        "warning" => 1,
        "ok" => 2,
        _ => 3,
    };

    /// <summary>
    /// The sensor class a tool argument names. LibreNMS's own class names
    /// work, plus the words people use: fan, optics, temp.
    /// </summary>
    public static string? ParseType(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => null,
        "temp" or "temps" or "temperatures" => "temperature",
        "fan" or "fans" or "fan speed" => "fanspeed",
        "optic" or "optics" or "optical" or "light" or "rx" or "tx" => "dbm",
        "volts" => "voltage",
        "amps" => "current",
        "watts" => "power",
        var other => other,
    };
}
