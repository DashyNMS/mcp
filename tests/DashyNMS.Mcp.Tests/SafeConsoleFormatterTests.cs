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
    public void Nothing_invisible_or_line_breaking_gets_through()
    {
        // A line feed, an escape, a line separator, a right-to-left override and a tag character outside the basic plane.
        var line = _formatter.OneLine("a\nb\u001bc\u2028d\u202ee\U000E0041f");

        Assert.Equal("a b c d e f", line);
    }

    [Fact]
    public void No_part_of_a_credential_shows_wherever_the_cut_falls()
    {
        for (var before = SafeConsoleFormatter.MaxLength - Secret.Length - 2; before <= SafeConsoleFormatter.MaxLength + 2; before++)
        {
            var line = _formatter.OneLine(new string('x', before) + Secret + new string('y', 100));

            Assert.DoesNotContain("synt", line, StringComparison.Ordinal);
            Assert.True(line.Length <= SafeConsoleFormatter.MaxLength + 1, $"{line.Length} characters");
        }
    }

    [Fact]
    public void A_credential_is_still_cut_out_when_others_before_it_made_the_line_shorter()
    {
        var text = string.Concat(Enumerable.Repeat(Secret + " ", 500));

        Assert.DoesNotContain("synt", _formatter.OneLine(text), StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_inside_and_beside_each_other_are_cut_out_whole()
    {
        const string inner = "abcdef";
        const string outer = "UNIQUEVALUE" + inner + inner + inner + inner + inner;
        const string beside = "SEPARATE" + "UNIQUE";
        var formatter = new SafeConsoleFormatter(new AuditRedactor(new Redactor(), [inner, outer, beside]));

        for (var before = SafeConsoleFormatter.MaxLength - outer.Length - beside.Length - 2; before <= SafeConsoleFormatter.MaxLength + 2; before++)
        {
            foreach (var credentials in new[] { outer, beside + outer, outer + beside, inner + outer })
            {
                var line = formatter.OneLine(new string('x', before) + credentials + new string('y', 100));

                Assert.DoesNotContain("UNIQ", line, StringComparison.Ordinal);
                Assert.DoesNotContain("SEPA", line, StringComparison.Ordinal);
                Assert.DoesNotContain("abc", line, StringComparison.Ordinal);
                Assert.True(line.Length <= SafeConsoleFormatter.MaxLength + 1, $"{line.Length} characters");
            }
        }
    }

    [Fact]
    public void Redactions_longer_than_what_they_hide_do_not_make_the_line_longer()
    {
        var formatter = new SafeConsoleFormatter(new AuditRedactor(new Redactor(), ["abcdef"]));

        var line = formatter.OneLine(string.Concat(Enumerable.Repeat("abcdef ", 3000)));

        Assert.DoesNotContain("abc", line, StringComparison.Ordinal);
        Assert.Equal(SafeConsoleFormatter.MaxLength + 1, line.Length);
    }
}
