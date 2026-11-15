namespace DashyNMS.Agent.Catalogue;

/// <summary>The JSON types a tool parameter takes.</summary>
public enum ToolParameterType
{
    String,
    Integer,
    Boolean,
    StringList,
}

/// <summary>One tool parameter as MCP lists it.</summary>
/// <param name="Name">The argument's name, exactly as a model sends it.</param>
/// <param name="Type">Its JSON type. An optional parameter without a <see cref="Default"/> also accepts null.</param>
/// <param name="Description">MCP's description, from <see cref="ToolText"/>.</param>
public sealed record ToolParameter(string Name, ToolParameterType Type, string Description)
{
    /// <summary>True when the tool can't run without it.</summary>
    public bool Required { get; init; }

    /// <summary>
    /// The default MCP's schema declares: an <see cref="int"/> or <see cref="bool"/>, or null. Null means
    /// leaving the argument out is "no filter" or "the tool decides", and some descriptions then name the
    /// tool's choice ("Rank by total (the default)"). It stays null here because MCP's schema says null,
    /// and the description, which a host passes on unchanged, already tells the model.
    /// </summary>
    public object? Default { get; init; }

    /// <summary>
    /// The values the description documents, for a string or each item of a string list. Null when
    /// any text is accepted. MCP's tools also take a few aliases (<c>all</c> for <c>both</c>, say);
    /// these are the words to offer a model.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>The smallest integer accepted, when the description gives one.</summary>
    public int? Minimum { get; init; }

    /// <summary>The largest integer accepted, when the description gives one.</summary>
    public int? Maximum { get; init; }
}

/// <summary>One tool as MCP lists it.</summary>
/// <param name="Name">The tool's name, as a model calls it.</param>
/// <param name="Title">The short title MCP clients show.</param>
/// <param name="Description">MCP's description, from <see cref="ToolText"/>.</param>
/// <param name="Parameters">In MCP's order.</param>
/// <param name="NamedTools">
/// The other tools this tool's description or its parameters' descriptions point a model at, in
/// catalogue order. A host offering only some tools can tell from it when a description names one
/// the model can't call.
/// </param>
public sealed record ToolDefinition(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<ToolParameter> Parameters,
    IReadOnlyList<string> NamedTools);

/// <summary>
/// Every tool DashyNMS's MCP server lists, with the same names, descriptions and parameters, for hosts
/// that describe the tools to a model without MCP.
/// </summary>
/// <remarks>
/// <para>
/// DashyNMS mobile runs the shared typed operations in process and has to describe each tool to Apple's
/// model itself. Reading this rather than writing its own text means the model on the phone is told
/// exactly what the model on desktop is told (DashyNMS/mcp#130). A host may leave out tools or
/// parameters it doesn't support yet, but never rewords them.
/// </para>
/// <para>
/// MCP's attributes read the same <see cref="ToolText"/> constants, and MCP's tests hold its listed tools,
/// with their JSON schemas, equal to this catalogue, so the two can't drift. MCP keeps its own
/// registration and JSON output; this is a library it reads, not a replacement for <c>tools/list</c>.
/// </para>
/// </remarks>
public static class ToolCatalogue
{
    /// <summary>Every tool, in the order a person meets them: finding and overview first, integrations and graphs last.</summary>
    public static IReadOnlyList<ToolDefinition> Tools { get; } =
    [
        new(ToolText.FindDevice.Name, ToolText.FindDevice.Title, ToolText.FindDevice.Description,
            [
                new("query", ToolParameterType.String, ToolText.FindDevice.Args.Query),
                new("status", ToolParameterType.String, ToolText.FindDevice.Args.Status) { AllowedValues = ["up", "down", "disabled"] },
                new("group", ToolParameterType.String, ToolText.FindDevice.Args.Group),
                new("location", ToolParameterType.String, ToolText.FindDevice.Args.Location),
                new("offset", ToolParameterType.Integer, ToolText.FindDevice.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.FindDevice.Args.Limit) { Default = 10, Minimum = 1, Maximum = 50 },
                new("verbose", ToolParameterType.Boolean, ToolText.FindDevice.Args.Verbose) { Default = false },
            ],
            [ToolText.ListGroupsAndLocations.Name]),
        new(ToolText.NetworkOverview.Name, ToolText.NetworkOverview.Title, ToolText.NetworkOverview.Description, [],
            [ToolText.FindDevice.Name, ToolText.ActiveAlerts.Name, ToolText.TopInterfaces.Name, ToolText.TopErrors.Name, ToolText.TopDevices.Name]),
        new(ToolText.ActiveAlerts.Name, ToolText.ActiveAlerts.Title, ToolText.ActiveAlerts.Description,
            [
                new("severity", ToolParameterType.String, ToolText.ActiveAlerts.Args.Severity) { AllowedValues = ["critical", "warning", "ok"] },
                new("device", ToolParameterType.String, ToolText.ActiveAlerts.Args.Device),
                new("group", ToolParameterType.String, ToolText.ActiveAlerts.Args.Group),
                new("location", ToolParameterType.String, ToolText.ActiveAlerts.Args.Location),
                new("hours", ToolParameterType.Integer, ToolText.ActiveAlerts.Args.Hours) { Minimum = 1, Maximum = 168 },
                new("since", ToolParameterType.String, ToolText.ActiveAlerts.Args.Since),
                new("until", ToolParameterType.String, ToolText.ActiveAlerts.Args.Until),
                new("offset", ToolParameterType.Integer, ToolText.ActiveAlerts.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.ActiveAlerts.Args.Limit) { Default = 50, Minimum = 1, Maximum = 100 },
                new("verbose", ToolParameterType.Boolean, ToolText.ActiveAlerts.Args.Verbose) { Default = false },
            ],
            [ToolText.ListGroupsAndLocations.Name, ToolText.AlertDetails.Name]),
        new(ToolText.DeviceHealth.Name, ToolText.DeviceHealth.Title, ToolText.DeviceHealth.Description,
            [
                new("device", ToolParameterType.String, ToolText.DeviceHealth.Args.Device) { Required = true },
            ],
            [ToolText.PortStatus.Name, ToolText.DeviceEvents.Name, ToolText.WhatChanged.Name]),
        new(ToolText.DeviceSensors.Name, ToolText.DeviceSensors.Title, ToolText.DeviceSensors.Description,
            [
                new("device", ToolParameterType.String, ToolText.DeviceSensors.Args.Device) { Required = true },
                new("type", ToolParameterType.String, ToolText.DeviceSensors.Args.Type),
            ],
            [ToolText.HealthOverview.Name]),
        new(ToolText.ListGroupsAndLocations.Name, ToolText.ListGroupsAndLocations.Title, ToolText.ListGroupsAndLocations.Description, [],
            [ToolText.FindDevice.Name, ToolText.ActiveAlerts.Name]),
        new(ToolText.AlertDetails.Name, ToolText.AlertDetails.Title, ToolText.AlertDetails.Description,
            [
                new("alert_id", ToolParameterType.Integer, ToolText.AlertDetails.Args.AlertId) { Required = true },
            ],
            [ToolText.ActiveAlerts.Name]),
        new(ToolText.AlertRules.Name, ToolText.AlertRules.Title, ToolText.AlertRules.Description,
            [
                new("name", ToolParameterType.String, ToolText.AlertRules.Args.Name),
                new("severity", ToolParameterType.String, ToolText.AlertRules.Args.Severity) { AllowedValues = ["critical", "warning", "ok"] },
                new("include_disabled", ToolParameterType.Boolean, ToolText.AlertRules.Args.IncludeDisabled) { Default = false },
                new("sort", ToolParameterType.String, ToolText.AlertRules.Args.Sort) { AllowedValues = ["name", "open", "raised"] },
                new("offset", ToolParameterType.Integer, ToolText.AlertRules.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.AlertRules.Args.Limit) { Default = 50, Minimum = 1, Maximum = 100 },
                new("verbose", ToolParameterType.Boolean, ToolText.AlertRules.Args.Verbose) { Default = false },
            ],
            []),
        new(ToolText.PortStatus.Name, ToolText.PortStatus.Title, ToolText.PortStatus.Description,
            [
                new("device", ToolParameterType.String, ToolText.PortStatus.Args.Device) { Required = true },
                new("port", ToolParameterType.String, ToolText.PortStatus.Args.Port) { Required = true },
            ],
            [ToolText.DeviceHealth.Name]),
        new(ToolText.DeviceNeighbours.Name, ToolText.DeviceNeighbours.Title, ToolText.DeviceNeighbours.Description,
            [
                new("device", ToolParameterType.String, ToolText.DeviceNeighbours.Args.Device) { Required = true },
            ],
            [ToolText.PortStatus.Name]),
        new(ToolText.TraceEndpoint.Name, ToolText.TraceEndpoint.Title, ToolText.TraceEndpoint.Description,
            [
                new("address", ToolParameterType.String, ToolText.TraceEndpoint.Args.Address) { Required = true },
            ],
            []),
        new(ToolText.DeviceDetails.Name, ToolText.DeviceDetails.Title, ToolText.DeviceDetails.Description,
            [
                new("device", ToolParameterType.String, ToolText.DeviceDetails.Args.Device) { Required = true },
                new("sections", ToolParameterType.StringList, ToolText.DeviceDetails.Args.Sections) { AllowedValues = ["inventory", "vlans", "fdb", "arp", "poe", "wireless", "routing", "availability", "addresses"] },
                new("offset", ToolParameterType.Integer, ToolText.DeviceDetails.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.DeviceDetails.Args.Limit) { Default = 50, Minimum = 1, Maximum = 200 },
            ],
            [ToolText.DeviceHealth.Name, ToolText.DeviceSensors.Name, ToolText.DeviceNeighbours.Name]),
        new(ToolText.TopInterfaces.Name, ToolText.TopInterfaces.Title, ToolText.TopInterfaces.Description,
            [
                new("by", ToolParameterType.String, ToolText.TopInterfaces.Args.By) { AllowedValues = ["total", "in", "out"] },
                new("limit", ToolParameterType.Integer, ToolText.TopInterfaces.Args.Limit) { Default = 10, Minimum = 1, Maximum = 50 },
                new("group", ToolParameterType.String, ToolText.TopInterfaces.Args.Group),
                new("location", ToolParameterType.String, ToolText.TopInterfaces.Args.Location),
            ],
            []),
        new(ToolText.TopErrors.Name, ToolText.TopErrors.Title, ToolText.TopErrors.Description,
            [
                new("by", ToolParameterType.String, ToolText.TopErrors.Args.By) { AllowedValues = ["total", "in", "out"] },
                new("limit", ToolParameterType.Integer, ToolText.TopErrors.Args.Limit) { Default = 10, Minimum = 1, Maximum = 50 },
                new("group", ToolParameterType.String, ToolText.TopErrors.Args.Group),
                new("location", ToolParameterType.String, ToolText.TopErrors.Args.Location),
            ],
            []),
        new(ToolText.TopDevices.Name, ToolText.TopDevices.Title, ToolText.TopDevices.Description,
            [
                new("by", ToolParameterType.String, ToolText.TopDevices.Args.By) { AllowedValues = ["traffic", "in", "out", "problems"] },
                new("limit", ToolParameterType.Integer, ToolText.TopDevices.Args.Limit) { Default = 10, Minimum = 1, Maximum = 50 },
                new("group", ToolParameterType.String, ToolText.TopDevices.Args.Group),
                new("location", ToolParameterType.String, ToolText.TopDevices.Args.Location),
            ],
            []),
        new(ToolText.HealthOverview.Name, ToolText.HealthOverview.Title, ToolText.HealthOverview.Description,
            [
                new("type", ToolParameterType.String, ToolText.HealthOverview.Args.Type),
                new("only_problems", ToolParameterType.Boolean, ToolText.HealthOverview.Args.OnlyProblems) { Default = true },
                new("group", ToolParameterType.String, ToolText.HealthOverview.Args.Group),
                new("location", ToolParameterType.String, ToolText.HealthOverview.Args.Location),
                new("offset", ToolParameterType.Integer, ToolText.HealthOverview.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.HealthOverview.Args.Limit) { Default = 50, Minimum = 1, Maximum = 100 },
            ],
            [ToolText.DeviceSensors.Name, ToolText.ListGroupsAndLocations.Name]),
        new(ToolText.DeviceEvents.Name, ToolText.DeviceEvents.Title, ToolText.DeviceEvents.Description,
            [
                new("device", ToolParameterType.String, ToolText.DeviceEvents.Args.Device) { Required = true },
                new("hours", ToolParameterType.Integer, ToolText.DeviceEvents.Args.Hours) { Minimum = 1, Maximum = 168 },
                new("since", ToolParameterType.String, ToolText.DeviceEvents.Args.Since),
                new("until", ToolParameterType.String, ToolText.DeviceEvents.Args.Until),
                new("offset", ToolParameterType.Integer, ToolText.DeviceEvents.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.DeviceEvents.Args.Limit) { Default = 50, Minimum = 1, Maximum = 100 },
            ],
            [ToolText.WhatChanged.Name]),
        new(ToolText.NetworkEvents.Name, ToolText.NetworkEvents.Title, ToolText.NetworkEvents.Description,
            [
                new("log", ToolParameterType.String, ToolText.NetworkEvents.Args.Log) { AllowedValues = ["events", "alerts", "both"] },
                new("hours", ToolParameterType.Integer, ToolText.NetworkEvents.Args.Hours) { Minimum = 1, Maximum = 168 },
                new("since", ToolParameterType.String, ToolText.NetworkEvents.Args.Since),
                new("until", ToolParameterType.String, ToolText.NetworkEvents.Args.Until),
                new("type", ToolParameterType.String, ToolText.NetworkEvents.Args.Type),
                new("severity", ToolParameterType.String, ToolText.NetworkEvents.Args.Severity) { AllowedValues = ["ok", "info", "notice", "warning", "critical"] },
                new("device", ToolParameterType.String, ToolText.NetworkEvents.Args.Device),
                new("group", ToolParameterType.String, ToolText.NetworkEvents.Args.Group),
                new("location", ToolParameterType.String, ToolText.NetworkEvents.Args.Location),
                new("offset", ToolParameterType.Integer, ToolText.NetworkEvents.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.NetworkEvents.Args.Limit) { Default = 50, Minimum = 1, Maximum = 100 },
            ],
            [ToolText.DeviceEvents.Name]),
        new(ToolText.WhatChanged.Name, ToolText.WhatChanged.Title, ToolText.WhatChanged.Description,
            [
                new("device", ToolParameterType.String, ToolText.WhatChanged.Args.Device) { Required = true },
                new("hours", ToolParameterType.Integer, ToolText.WhatChanged.Args.Hours) { Minimum = 1, Maximum = 168 },
                new("since", ToolParameterType.String, ToolText.WhatChanged.Args.Since),
                new("until", ToolParameterType.String, ToolText.WhatChanged.Args.Until),
            ],
            [ToolText.DeviceEvents.Name]),
        new(ToolText.SearchLogs.Name, ToolText.SearchLogs.Title, ToolText.SearchLogs.Description,
            [
                new("text", ToolParameterType.String, ToolText.SearchLogs.Args.Text),
                new("device", ToolParameterType.String, ToolText.SearchLogs.Args.Device),
                new("since", ToolParameterType.String, ToolText.SearchLogs.Args.Since),
                new("until", ToolParameterType.String, ToolText.SearchLogs.Args.Until),
                new("hours", ToolParameterType.Integer, ToolText.SearchLogs.Args.Hours) { Minimum = 1, Maximum = 168 },
                new("level", ToolParameterType.Integer, ToolText.SearchLogs.Args.Level) { Minimum = 0, Maximum = 7 },
                new("stream", ToolParameterType.String, ToolText.SearchLogs.Args.Stream),
                new("limit", ToolParameterType.Integer, ToolText.SearchLogs.Args.Limit) { Default = 20, Minimum = 0, Maximum = 50 },
            ],
            [ToolText.WhatChanged.Name]),
        new(ToolText.ConfigBackup.Name, ToolText.ConfigBackup.Title, ToolText.ConfigBackup.Description,
            [
                new("device", ToolParameterType.String, ToolText.ConfigBackup.Args.Device) { Required = true },
                new("backup_id", ToolParameterType.Integer, ToolText.ConfigBackup.Args.BackupId),
                new("offset", ToolParameterType.Integer, ToolText.ConfigBackup.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.ConfigBackup.Args.Limit) { Default = 200, Minimum = 1, Maximum = 1000 },
            ],
            [ToolText.ConfigDiff.Name]),
        new(ToolText.ConfigDiff.Name, ToolText.ConfigDiff.Title, ToolText.ConfigDiff.Description,
            [
                new("device", ToolParameterType.String, ToolText.ConfigDiff.Args.Device) { Required = true },
                new("from_backup", ToolParameterType.Integer, ToolText.ConfigDiff.Args.FromBackup),
                new("to_backup", ToolParameterType.Integer, ToolText.ConfigDiff.Args.ToBackup),
            ],
            [ToolText.WhatChanged.Name, ToolText.ConfigBackup.Name]),
        new(ToolText.RecentConfigChanges.Name, ToolText.RecentConfigChanges.Title, ToolText.RecentConfigChanges.Description,
            [
                new("hours", ToolParameterType.Integer, ToolText.RecentConfigChanges.Args.Hours) { Minimum = 1, Maximum = 168 },
                new("since", ToolParameterType.String, ToolText.RecentConfigChanges.Args.Since),
                new("until", ToolParameterType.String, ToolText.RecentConfigChanges.Args.Until),
                new("group", ToolParameterType.String, ToolText.RecentConfigChanges.Args.Group),
                new("location", ToolParameterType.String, ToolText.RecentConfigChanges.Args.Location),
                new("offset", ToolParameterType.Integer, ToolText.RecentConfigChanges.Args.Offset) { Default = 0, Minimum = 0 },
                new("limit", ToolParameterType.Integer, ToolText.RecentConfigChanges.Args.Limit) { Default = 50, Minimum = 1, Maximum = 100 },
            ],
            [ToolText.ConfigDiff.Name]),
        new(ToolText.Graph.Name, ToolText.Graph.Title, ToolText.Graph.Description,
            [
                new("device", ToolParameterType.String, ToolText.Graph.Args.Device) { Required = true },
                new("type", ToolParameterType.String, ToolText.Graph.Args.Type),
                new("port", ToolParameterType.String, ToolText.Graph.Args.Port),
                new("sensor", ToolParameterType.String, ToolText.Graph.Args.Sensor),
                new("range", ToolParameterType.String, ToolText.Graph.Args.Range) { AllowedValues = ["hour", "day", "week", "month", "year"] },
                new("since", ToolParameterType.String, ToolText.Graph.Args.Since),
                new("until", ToolParameterType.String, ToolText.Graph.Args.Until),
                new("legend", ToolParameterType.Boolean, ToolText.Graph.Args.Legend) { Default = true },
            ],
            [ToolText.DeviceHealth.Name, ToolText.DeviceSensors.Name, ToolText.PortStatus.Name]),
    ];

    private static readonly Dictionary<string, ToolDefinition> ByName = Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>Every tool's name, in catalogue order.</summary>
    public static IReadOnlyList<string> Names { get; } = Tools.Select(t => t.Name).ToArray();

    /// <summary>The tool with this exact name, or null. Names are matched case-sensitively, as MCP calls them.</summary>
    public static ToolDefinition? Find(string name)
        => name is not null && ByName.TryGetValue(name, out var tool) ? tool : null;
}
