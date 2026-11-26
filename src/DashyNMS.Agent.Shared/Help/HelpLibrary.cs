using System.Reflection;
using System.Text.Json;
using DesktopNMS.Core.Help;

namespace DashyNMS.Agent.Help;

/// <summary>
/// The DashyNMS user guides a model can be asked about (#164): Desktop's, Mobile's
/// and this server's, bundled in this assembly so they need no connection of any kind.
/// </summary>
/// <remarks>
/// Nothing is ever requested unless the host turns refreshing on, because it adds GitHub
/// as a destination. Then each guide is fetched at its own release's tag, in the
/// background, with nothing about the host, its network or a question in the request; a
/// call is always answered from what's already here.
/// </remarks>
public sealed class HelpLibrary : IDisposable
{
    private const string ResourcePrefix = "DashyNMS.Agent.Help/";

    private readonly IReadOnlyList<HelpGuide> bundled;
    private readonly IReadOnlyList<HelpGuideService> services;
    private int refreshing;

    /// <param name="serverVersion">The MCP server's version, which its own guide describes; null for this assembly's.</param>
    /// <param name="refresh">Whether guides may be fetched from GitHub. Off by default.</param>
    /// <param name="http">A client to fetch with, or null for one of Core's own. It must not follow redirects.</param>
    public HelpLibrary(string? serverVersion = null, bool refresh = false, HttpClient? http = null)
    {
        var assembly = typeof(HelpLibrary).Assembly;
        var versions = Manifest(assembly);
        var guides = new List<HelpGuide>();
        var refreshable = new List<HelpGuideService>();
        foreach (var product in HelpProduct.All)
        {
            var files = HelpFileSet.FromAssembly(assembly, ResourcePrefix + product.Id + "/");
            HelpRelease? bundledRelease;
            HelpRelease? tag;
            if (product == HelpProduct.Mcp)
            {
                bundledRelease = tag = HelpRelease.ForVersion(serverVersion
                    ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            }
            else
            {
                var snapshot = versions[product.Id];
                bundledRelease = HelpRelease.ForReference(snapshot.Version, snapshot.Reference);
                tag = HelpRelease.ForVersion(snapshot.Version);
            }

            guides.Add(HelpGuide.Load(product, bundledRelease, files));
            if (refresh)
            {
                refreshable.Add(new HelpGuideService(product, tag, files, http: http, includeImages: false));
            }
        }

        bundled = guides;
        services = refreshable;
    }

    /// <summary>Guides of a test's or host's own.</summary>
    public HelpLibrary(IEnumerable<HelpGuide> guides)
    {
        ArgumentNullException.ThrowIfNull(guides);
        bundled = guides.ToArray();
        services = [];
    }

    /// <summary>The guides as they are now.</summary>
    public IReadOnlyList<HelpGuide> Guides
        => services.Count == 0
            ? bundled
            : bundled.Select((guide, index) => services[index].Source == HelpGuideSource.Downloaded ? services[index].Current : guide).ToArray();

    /// <summary>Starts a refresh when one is due and allowed. Never waits for it, and never throws.</summary>
    public void RefreshInBackground()
    {
        // One at a time: a slow one mustn't gather a queue of callers behind it.
        if (services.Count > 0 && Interlocked.Exchange(ref refreshing, 1) == 0)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await Task.WhenAll(services.Select(RefreshAsync)).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref refreshing, 0);
        }
    }

    private static async Task RefreshAsync(HelpGuideService service)
    {
        try
        {
            await Task.Run(() => service.RefreshAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server is stopping.
        }
        catch (ObjectDisposedException)
        {
            // The same.
        }
    }

    private static Dictionary<string, Snapshot> Manifest(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + "guides.json")
            ?? throw new InvalidOperationException("The bundled guides' manifest is missing.");
        return JsonSerializer.Deserialize(stream, HelpManifestJson.Default.DictionaryStringSnapshot)
            ?? throw new InvalidOperationException("The bundled guides' manifest is empty.");
    }

    public void Dispose()
    {
        foreach (var service in services)
        {
            service.Dispose();
        }
    }

    /// <summary>Where a bundled guide was copied from - see scripts/update-guides.py.</summary>
    internal sealed record Snapshot(
        [property: System.Text.Json.Serialization.JsonPropertyName("version")] string Version,
        [property: System.Text.Json.Serialization.JsonPropertyName("reference")] string Reference);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, HelpLibrary.Snapshot>))]
internal sealed partial class HelpManifestJson : System.Text.Json.Serialization.JsonSerializerContext;
