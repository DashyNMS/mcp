using System.Globalization;
using System.Text;
using DashyNMS.Mcp.Auditing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace DashyNMS.Mcp;

/// <summary>
/// Writes each log entry as one line, with nothing in it that could pass for
/// another line or for a secret.
/// </summary>
/// <remarks>
/// The MCP SDK logs what clients call themselves and the tools they ask for,
/// as they sent them. The usual formatter would write a line break in a name
/// as one, and stderr also carries the audit trail (#18): a client could write
/// an entry naming somebody else.
/// </remarks>
internal sealed class SafeConsoleFormatter(AuditRedactor redactor) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "dashynms";

    /// <summary>Room for a stack trace; a client can send names far longer.</summary>
    internal const int MaxLength = 8000;

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        var text = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (logEntry.Exception is { } exception)
        {
            text = $"{text} | {exception}";
        }

        textWriter.WriteLine($"{Level(logEntry.LogLevel)}: {logEntry.Category}: {OneLine(text)}");
    }

    internal string OneLine(string text)
    {
        // Cut before the patterns run, so a huge name costs no more than a long
        // one, and again after, as a redaction can be longer than what it hides.
        var clean = redactor.Scrub(redactor.Start(text, MaxLength));
        if (clean.Length > MaxLength)
        {
            clean = clean[..MaxLength];
        }

        if (text.Length > MaxLength)
        {
            clean += "…";
        }

        var line = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            // Control characters, line and paragraph separators, and the invisible ones that reorder text.
            line.Append(CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format
                ? ' '
                : c);
        }

        return line.ToString();
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        _ => "crit",
    };
}
