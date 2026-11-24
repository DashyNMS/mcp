namespace DashyNMS.Mcp.Auditing;

/// <summary>Somewhere audit entries go. It must not throw or block: recording a call never gets in its way.</summary>
public interface IAuditSink
{
    void Write(AuditEntry entry);
}

/// <summary>
/// Audit entries as JSON lines on stderr, beside the server's other logs.
/// </summary>
/// <remarks>
/// Written directly rather than through the logging framework: its console
/// formatter splits an entry over two lines and its level filters could drop
/// it. stdout isn't an option, as it carries the MCP protocol over stdio.
/// </remarks>
public sealed class StderrAuditSink : IAuditSink
{
    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            Console.Error.WriteLine(entry.ToJson());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nobody is reading stderr any more. There's nowhere left to say so.
        }
    }
}
