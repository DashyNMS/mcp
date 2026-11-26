namespace DashyNMS.Mcp.Auditing;

/// <summary>Where audit entries are sent: a Graylog GELF HTTP or UDP input.</summary>
public sealed class GelfTarget
{
    /// <summary>The port Graylog's GELF inputs use unless told otherwise.</summary>
    public const int DefaultPort = 12201;

    private GelfTarget(Uri address, IReadOnlyCollection<string> trustedCertificates)
    {
        Address = address;
        TrustedCertificates = trustedCertificates;
    }

    public Uri Address { get; }

    /// <summary>Fingerprints of certificates to trust when the normal checks reject them, as for Graylog itself.</summary>
    public IReadOnlyCollection<string> TrustedCertificates { get; }

    public bool IsUdp => Address.Scheme == "udp";

    /// <summary>Whether entries cross the network where anyone on the way can read them.</summary>
    public bool IsUnencrypted => Address.Scheme != Uri.UriSchemeHttps && !Address.IsLoopback;

    /// <summary>
    /// Reads the address, or null when it isn't one. Credentials in it are
    /// refused: GELF inputs take none, and a URL is easily logged.
    /// </summary>
    public static GelfTarget? TryParse(string text, IReadOnlyCollection<string> trustedCertificates)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "udp")
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return null;
        }

        if (uri.Scheme == "udp")
        {
            // An input has a port and nothing else, so a path is a mistake.
            return uri.AbsolutePath is "/" or "" ? new GelfTarget(WithPort(uri), trustedCertificates) : null;
        }

        // Graylog's GELF HTTP input answers at /gelf; a reverse proxy can put it elsewhere.
        return new GelfTarget(uri.AbsolutePath == "/" ? new Uri(uri, "/gelf") : uri, trustedCertificates);
    }

    private static Uri WithPort(Uri uri) => uri.Port > 0 ? uri : new UriBuilder(uri) { Port = DefaultPort }.Uri;
}
