using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>A device filter from a tool's <c>group</c> and <c>location</c> arguments.</summary>
public sealed class Scope
{
    public static readonly Scope Everything = new(null, null);

    private readonly IReadOnlySet<int>? _groupMembers;
    private readonly string? _location;

    internal Scope(IReadOnlySet<int>? groupMembers, string? location)
    {
        _groupMembers = groupMembers;
        _location = location;
    }

    public bool IsEverything => _groupMembers is null && _location is null;

    public bool Includes(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return (_groupMembers is null || _groupMembers.Contains(device.DeviceId))
            && (_location is null || string.Equals(device.Location?.Trim(), _location, StringComparison.OrdinalIgnoreCase));
    }

    public bool Includes(int deviceId, IReadOnlyDictionary<int, Device> devices)
        => devices.TryGetValue(deviceId, out var device) ? Includes(device) : IsEverything;
}

/// <summary>
/// Turns a device group or location name into a <see cref="Scope"/> (#34), as
/// desktop's group and location filters do.
/// </summary>
/// <remarks>
/// LibreNMS has no bulk "groups per device" call, but one group's members are
/// a single request, so a filter fetches only the group asked for. A device's
/// location is a text field on the device itself, so filtering by one costs
/// nothing beyond the cached device list. Names match exactly first, then as
/// part of a name when that's unambiguous; anything else is an error that
/// lists the names, so the model asks rather than guesses.
/// </remarks>
public sealed class DeviceScope(ILibreNmsClient client, DeviceDirectory directory, TimeProvider time)
{
    public const int MaxCandidates = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DeviceGroup>? _groups;
    private DateTimeOffset _groupsFetchedAt;

    public async Task<Scope> ResolveAsync(string? group, string? location, CancellationToken cancellationToken)
    {
        var groupName = Blank(group);
        var locationName = Blank(location);
        if (groupName is null && locationName is null)
        {
            return Scope.Everything;
        }

        IReadOnlySet<int>? members = null;
        if (groupName is not null)
        {
            var match = Pick(await ListGroupsAsync(cancellationToken).ConfigureAwait(false), g => g.Name, groupName, "device group");
            members = (await client.DeviceGroups.GetMemberDeviceIdsAsync(match.Id, cancellationToken).ConfigureAwait(false)).ToHashSet();
        }

        string? resolvedLocation = null;
        if (locationName is not null)
        {
            var devices = await directory.GetAllAsync(cancellationToken).ConfigureAwait(false);
            resolvedLocation = Pick(Locations(devices), l => l, locationName, "location");
        }

        return new Scope(members, resolvedLocation);
    }

    /// <summary>The device groups LibreNMS has, cached for as long as the device list.</summary>
    public async Task<IReadOnlyList<DeviceGroup>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } groups)
        {
            return groups;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } fetched)
            {
                return fetched;
            }

            var list = await client.DeviceGroups.ListAsync(cancellationToken).ConfigureAwait(false);
            _groups = list;
            _groupsFetchedAt = time.GetUtcNow();
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every location name in use on a device, as LibreNMS's devices carry it.</summary>
    public static IReadOnlyList<string> Locations(IEnumerable<Device> devices)
        => devices
            .Select(d => d.Location?.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => l!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<DeviceGroup>? Fresh()
        => _groups is { } groups && time.GetUtcNow() - _groupsFetchedAt < DeviceDirectory.Lifetime ? groups : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static T Pick<T>(IReadOnlyList<T> items, Func<T, string> name, string text, string kind)
    {
        var exact = items.Where(i => string.Equals(name(i), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = exact.Count > 1 ? exact : items.Where(i => name(i).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            return partial[0];
        }

        if (partial.Count == 0)
        {
            throw new ToolException($"No {kind} matches. list_groups_and_locations shows the names that exist.");
        }

        throw new ToolException(
            $"{partial.Count} {kind}s match. Ask which one is meant.",
            new { candidates = partial.Take(MaxCandidates).Select(name).ToList(), omitted = Math.Max(0, partial.Count - MaxCandidates) });
    }
}
