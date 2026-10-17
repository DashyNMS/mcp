using ModelContextProtocol.Protocol;

namespace DashyNMS.Mcp;

/// <summary>
/// The DashyNMS logo as the server's MCP icon, so clients such as Claude
/// Desktop show it next to the server and its tools instead of a letter.
/// </summary>
/// <remarks>
/// Embedded as a data URI rather than linked: the client needn't fetch
/// anything from the internet to show it, and it works on networks that
/// can't reach the website.
/// </remarks>
internal static class ServerIcon
{
    private const string Resource = "DashyNMS.Mcp.icon.png";

    public static IList<Icon> All { get; } = Load();

    private static List<Icon> Load()
    {
        using var stream = typeof(ServerIcon).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The {Resource} resource is missing from the build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return
        [
            new Icon
            {
                Source = "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray()),
                MimeType = "image/png",
                Sizes = ["176x176"],
            },
        ];
    }
}
