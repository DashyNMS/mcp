using DashyNMS.Agent.Redaction;
using DashyNMS.Mcp.Auditing;

namespace DashyNMS.Mcp.Tests;

public sealed class SafeConsoleFormatterTests
{
    private const string Secret = "synthetic-credential-0123456789abcdef";

    private readonly SafeConsoleFormatter _formatter = new(new AuditRedactor(new Redactor(), [Secret]));

    [Fact]
    public void An_ordinary_line_is_as_it_was()
    {
        Assert.Equal("Connected to LibreNMS 26.10.0 at librenms.example.net", _formatter.OneLine("Connected to LibreNMS 26.10.0 at librenms.example.net"));
    }

    [Fact]
    public void No_part_of_a_credential_shows_wherever_the_cut_falls()
    {
        for (var before = SafeConsoleFormatter.MaxLength - Secret.Length - 2; before <= SafeConsoleFormatter.MaxLength + 2; before++)
        {
            var line = _formatter.OneLine(new string('x', before) + Secret + new string('y', 100));

            Assert.DoesNotContain("synt", line, StringComparison.Ordinal);
            Assert.True(line.Length <= SafeConsoleFormatter.MaxLength, $"{line.Length} characters");
        }
    }

    [Fact]
    public void A_credential_is_still_cut_out_when_others_before_it_made_the_line_shorter()
    {
        var text = string.Concat(Enumerable.Repeat(Secret + " ", 500));

        Assert.DoesNotContain("synt", _formatter.OneLine(text), StringComparison.Ordinal);
    }
}
