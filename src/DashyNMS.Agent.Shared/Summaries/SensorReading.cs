using System.Text.Json.Serialization;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>
/// What a reading's state was judged against. Hosts that show a state need to tell "unknown
/// because there was no reading" from "unknown because nothing says what's healthy", and both
/// from a judged reading, so none of them is ever presented as healthy by mistake.
/// </summary>
public enum SensorAssessment
{
    /// <summary>LibreNMS has no current value (not a zero): nothing to judge.</summary>
    NoReading,

    /// <summary>A value, but no limits of its own and no default for its class: state unknown.</summary>
    NoLimits,

    /// <summary>The sensor's own LibreNMS limits.</summary>
    SensorLimits,

    /// <summary>
    /// Core's host thresholds for dBm, signal, temperature or fan speed, with the sensor's own
    /// limits winning unless the host requests an override. A reading in the class's "nothing plugged in" band
    /// stays unknown.
    /// </summary>
    Thresholds,

    /// <summary>An installed state definition's severity, whatever the number.</summary>
    StateDefinition,
}

/// <summary>
/// One sensor's reading, judged the way desktop's Health tab judges it
/// (#24): against the sensor's own LibreNMS limits where it has them, and
/// the host's thresholds for dBm, signal, temperature and fans according to
/// its sensor-limit override choice (Core's <see cref="SensorCategoryRegistry"/>).
/// </summary>
/// <param name="State">ok, warning, critical, or unknown (no limits to judge by, or a "nothing plugged in" reading).</param>
public sealed record SensorReading(
    int SensorId,
    string Type,
    string? Description,
    double? Value,
    string? Unit,
    string State,
    double? LowCritical,
    double? LowWarning,
    double? HighWarning,
    double? HighCritical)
{
    public string? StateLabel { get; init; }
    public string? StateDefinitionVersion { get; init; }
    public string? StateDefinitionOutcome { get; init; }

    /// <summary>
    /// What <see cref="State"/> was judged against. Left out of JSON so MCP's output stays
    /// unchanged; MCP's limits and state label already say as much to a model.
    /// </summary>
    [JsonIgnore]
    public SensorAssessment Assessment { get; init; }

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

    public static SensorReading From(Sensor sensor) => From(sensor, Defaults);

    /// <summary>Uses the host's captured settings through Core's own evaluator.</summary>
    public static SensorReading From(Sensor sensor, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sensor);
        var bounds = new ThresholdBounds(sensor.LimitLow, sensor.LimitLowWarn, sensor.LimitHighWarn, sensor.LimitHigh);
        IThresholdEvaluator evaluator = SensorCategoryRegistry.Resolve(sensor.SensorClass) is { } entry
            ? entry.Thresholds(settings, sensor)
            : new HybridThresholdEvaluator(default, bounds, overrideSensorLimitsWithAppThresholds: false);

        var hasLimits = SensorCategoryRegistry.Resolve(sensor.SensorClass) is not null
            || bounds.LowCritical is not null || bounds.LowWarning is not null || bounds.HighWarning is not null || bounds.HighCritical is not null;
        var severity = sensor.StateDefinition?.Severity ?? (sensor.HasReading && hasLimits ? evaluator.Evaluate(sensor.Current) : AlertSeverity.Unknown);
        var assessment = sensor.StateDefinition is not null ? SensorAssessment.StateDefinition
            : !sensor.HasReading ? SensorAssessment.NoReading
            : SensorCategoryRegistry.Resolve(sensor.SensorClass) is not null ? SensorAssessment.Thresholds
            : hasLimits ? SensorAssessment.SensorLimits
            : SensorAssessment.NoLimits;

        return new SensorReading(
            sensor.SensorId,
            sensor.SensorClass ?? "unknown",
            sensor.Description,
            sensor.HasReading ? Math.Round(sensor.Current!.Value, 2) : null,
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
            sensor.LimitHigh)
        { StateLabel = sensor.StateDefinition?.Label, StateDefinitionVersion = sensor.StateDefinition?.CatalogueVersion,
          StateDefinitionOutcome = sensor.StateDefinition?.Outcome.ToString(), Assessment = assessment };
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
