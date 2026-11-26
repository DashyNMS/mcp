using System.Text.Json.Serialization;
using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Graphs;
using DashyNMS.Agent.Graylog;
using DashyNMS.Agent.Health;
using DashyNMS.Agent.Help;
using DashyNMS.Agent.Overview;
using DashyNMS.Agent.Ports;
using DashyNMS.Agent.Rankings;
using DashyNMS.Agent.Summaries;
using DashyNMS.Agent.Unimus;

namespace DashyNMS.Agent.Outputs;

/// <summary>
/// The type behind each tool's <c>data</c>, which its output schema is made
/// from (#40). A tool with more than one lists them all.
/// </summary>
/// <remarks>
/// Short records (<see cref="DeviceBrief"/>, a fault without its times) are
/// the long ones with properties left out, so only the long one is listed.
/// </remarks>
internal static class ToolOutputs
{
    public static IReadOnlyList<Type> TypesOf(string tool) => Types[tool];

    private static readonly Dictionary<string, Type[]> Types = new(StringComparer.Ordinal)
    {
        [ToolText.ActiveAlerts.Name] = [typeof(ActiveAlertsData), typeof(RaisedAlertsData)],
        [ToolText.AlertDetails.Name] = [typeof(AlertDetailsData)],
        [ToolText.AlertRules.Name] = [typeof(ListedRulesData)],
        [ToolText.ConfigBackup.Name] = [typeof(ConfigBackupData)],
        [ToolText.ConfigDiff.Name] = [typeof(ConfigDiffData)],
        [ToolText.DashynmsHelp.Name] = [typeof(HelpSearchData), typeof(HelpPageData), typeof(HelpContentsData)],
        [ToolText.DeviceDetails.Name] = [typeof(DeviceDetailsData)],
        [ToolText.DeviceEvents.Name] = [typeof(DeviceEventsData)],
        [ToolText.DeviceHealth.Name] = [typeof(DeviceHealthData)],
        [ToolText.DeviceNeighbours.Name] = [typeof(DeviceNeighboursData)],
        [ToolText.DeviceSensors.Name] = [typeof(DeviceSensorsData)],
        [ToolText.FindDevice.Name] = [typeof(FoundDevices<DeviceSummary>), typeof(MacMatches<DeviceSummary>)],
        [ToolText.Graph.Name] = [typeof(GraphData)],
        [ToolText.HealthOverview.Name] = [typeof(HealthOverviewData)],
        [ToolText.ListGroupsAndLocations.Name] = [typeof(GroupsAndLocationsData)],
        [ToolText.NetworkEvents.Name] = [typeof(NetworkEventsData)],
        [ToolText.NetworkOverview.Name] = [typeof(NetworkOverviewData)],
        [ToolText.PortStatus.Name] = [typeof(PortStatusData)],
        [ToolText.RecentConfigChanges.Name] = [typeof(RecentConfigChangesData)],
        [ToolText.SearchLogs.Name] = [typeof(SearchLogsData)],
        [ToolText.TopDevices.Name] = [typeof(TopDevicesData)],
        [ToolText.TopErrors.Name] = [typeof(TopPortsData)],
        [ToolText.TopInterfaces.Name] = [typeof(TopPortsData)],
        [ToolText.TraceEndpoint.Name] = [typeof(TraceEndpointData)],
        [ToolText.WhatChanged.Name] = [typeof(WhatChangedData)],
    };
}

/// <summary>The types a property declared as <see cref="object"/> really holds.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class HoldsAttribute(params Type[] types) : Attribute
{
    public IReadOnlyList<Type> Types => types;
}

/// <param name="Since">When verbose.</param>
/// <param name="Faults">When there's more than one: the first few by name and state, or with everything when verbose.</param>
internal sealed record ActiveAlert(string GroupId, int AlertId, int RuleId, string Source, string Rule, string Severity, string State,
    int FaultCount, int? AcknowledgedCount, string? Entity, DateTime? Since, long? AgeMinutes, string? Note,
    IReadOnlyList<FaultBrief>? Faults, int? FaultsOmitted);

internal sealed record AlertingDevice(int DeviceId, string? Hostname, string? Display, string? DeviceStatus, IReadOnlyList<ActiveAlert> Alerts);

internal sealed record ActiveAlertsData(OpenAlertTotals Totals, IReadOnlyList<AlertingDevice> Devices, PageInfo Page);

internal sealed record FoundDevices<T>(IReadOnlyList<T> Devices, PageInfo Page);

internal sealed record MacMatch<T>(T Device, IReadOnlyList<ArpSighting> SeenAs);

internal sealed record MacMatches<T>(IReadOnlyList<MacMatch<T>> ArpMatches, PageInfo Page);

internal sealed record DeviceSensorsData(DeviceBrief Device, IReadOnlyList<SensorTypeCount> Types, IReadOnlyList<SensorReading> Readings);

/// <summary>A graph's facts beside its image, with the listings that couldn't be read.</summary>
internal sealed record GraphData(DeviceBrief Device, GraphMetadata? Graph, GraphSensorInfo? Sensor, GraphPortInfo? Port,
    IReadOnlyList<string>? OtherTypes, IReadOnlyList<GraphTypeInfo>? AvailableTypes, IReadOnlyList<string>? Unavailable);

internal sealed record OverviewAlerts(int Open, int Faults, int Critical, int Warning, int Acknowledged, int PartlyAcknowledged, int DevicesAlerting);

internal sealed record OverviewSource(string Source, string Error);

internal sealed record NetworkOverviewData(OverviewDeviceCounts Devices, IReadOnlyList<DownLocation> MostDownByLocation, OverviewAlerts? Alerts,
    OverviewPortCounts? Ports, string? LibrenmsVersion, IReadOnlyList<OverviewSource>? Unavailable, IReadOnlyList<DeviceSummary> DownDevices,
    IReadOnlyList<OpenAlertGroup> AlertGroups, int DownDevicesOmitted, int AlertGroupsOmitted, int DownLocationsOmitted);

internal sealed record ListedRuleTargets(bool? Except, IReadOnlyList<string>? Devices, IReadOnlyList<string>? Groups, IReadOnlyList<string>? Locations);

/// <param name="AppliesTo">"all devices", or what it's limited to.</param>
internal sealed record ListedRule(int RuleId, string Name, string Severity, bool Enabled, string? Condition,
    [property: Holds(typeof(string), typeof(ListedRuleTargets))] object AppliesTo,
    string? Template, bool? NotifyPerEntity, int? MaxEntities, bool? MaxEntitiesInherited, string? Acknowledging,
    IReadOnlyList<RemovedRuleColumn>? RemovedColumns, int? OpenAlerts, [property: JsonPropertyName("raised_last_7_days")] int? RaisedLast7Days,
    string? Notes, string? Procedure);

internal sealed record ListedRulesData(IReadOnlyList<ListedRule> Rules, PageInfo Page, IReadOnlyList<Unavailable>? Unavailable);
