using System.Text.Json.Serialization;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

public sealed record ScopeGroup(int GroupId, string? Name, string? Description, string? Type)
{
    /// <summary>Exact server name for host-side filtering only, never model text.</summary>
    [JsonIgnore]
    public string? FilterName { get; init; }
}
public sealed record ScopeLocation(int? LocationId, string Name, int Devices, int Down)
{
    /// <summary>Exact server name for host-side filtering only, never model text.</summary>
    [JsonIgnore]
    public string FilterName { get; init; } = Name;
}
public sealed record GroupsAndLocationsData(IReadOnlyList<ScopeGroup>? Groups, IReadOnlyList<ScopeLocation> Locations,
    int? DevicesWithoutLocation, IReadOnlyList<Unavailable>? Unavailable);
public sealed record GroupsAndLocationsResult(string Summary, GroupsAndLocationsData Data);

/// <summary>Filter names and counts from the same operation MCP runs.</summary>
/// <remarks>Use HostGroupsAndLocations for fixed failures and generation retirement.</remarks>
public sealed class GroupsAndLocations
{
    private readonly DeviceDirectory directory;
    private readonly DeviceScope scopes;
    private readonly Redactor redactor;

    internal GroupsAndLocations(DeviceDirectory directory, DeviceScope scopes, Redactor redactor)
    {
        this.directory = directory;
        this.scopes = scopes;
        this.redactor = redactor;
    }

    public static GroupsAndLocations Create(ILibreNmsClient client, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        var directory = new DeviceDirectory(client, time);
        return new(directory, new DeviceScope(client, directory, time), new Redactor());
    }

    public async Task<GroupsAndLocationsResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var groupsTask = Sources.TryAsync(() => scopes.ListGroupsAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(devicesTask, groupsTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var devices = devicesTask.Result;
        // Keep MCP's grouping and ordering on original text, before redaction.
        var locations = devices.Where(d => !string.IsNullOrWhiteSpace(d.Location))
            .GroupBy(d => d.Location!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScopeLocation(
                g.Select(d => d.LocationId).Distinct().ToArray() is [int id] && id > 0 ? id : null,
                redactor.Redact(g.Key), g.Count(), g.Count(d => DeviceSummary.StatusOf(d) == "down"))
                { FilterName = g.First().Location! }).ToList();
        var groups = groupsTask.Result.Value?.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScopeGroup(g.Id, Clean(g.Name), string.IsNullOrWhiteSpace(g.Description) ? null : Clean(g.Description), Clean(g.Type))
                { FilterName = g.Name }).ToList();
        var withoutLocation = devices.Count(d => string.IsNullOrWhiteSpace(d.Location));
        var data = new GroupsAndLocationsData(groups, locations, withoutLocation > 0 ? withoutLocation : null,
            groupsTask.Result.Error is { } error ? new[] { new Unavailable("device groups", error) } : null);
        var groupPart = groups is null ? "device groups couldn't be read" : DeviceHealth.Count(groups.Count, "device group");
        return new($"{groupPart[..1].ToUpperInvariant()}{groupPart[1..]} and {DeviceHealth.Count(locations.Count, "location")}.", data);
    }

    private string? Clean(string? text) => text is null ? null : redactor.Redact(text);
}
