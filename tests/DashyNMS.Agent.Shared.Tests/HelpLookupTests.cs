using System.Text;
using DashyNMS.Agent.Help;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Help;

namespace DashyNMS.Agent.Shared.Tests;

/// <summary>Answering from the user guides (#164). The guides here are made up.</summary>
public sealed class HelpLookupTests
{
    private static HelpGuide Guide(HelpProduct product, string version, params (string Path, string Text)[] pages)
        => HelpGuide.Load(product, HelpRelease.ForVersion(version), new HelpFileSet(pages.Select(page =>
            new KeyValuePair<string, byte[]>("docs/guide/" + page.Path, Encoding.UTF8.GetBytes(page.Text)))));

    private static HelpLookup Lookup() => new(new HelpLibrary(
    [
        Guide(HelpProduct.Desktop, "2.0.0",
            ("README.md", "# Guide\n\n## Tasks\n\n- [Widgets](widgets.md)\n- [Troubleshooting](troubleshooting.md)\n- [Long](long.md)"),
            ("widgets.md", "# Pin a widget\n\n![A widget](../widget.png)\n\nRight-click the widget. See [fixes](troubleshooting.md#stuck \"Fix it\"), [the site](https://example.net/a) and [gone](gone.md).\n\n## Remove it\n\nChoose **Unpin**.\n\n```\n[not a link](troubleshooting.md)\n```"),
            ("troubleshooting.md", "# Troubleshooting\n\n## Stuck\n\nUse password hunter2secret to restart the gadget."),
            ("long.md", "# Long page\n\n" + string.Join("\n", Enumerable.Repeat("A line about nothing much at all.", 600)) + "\n\n## End\n\nThe end.")),
        Guide(HelpProduct.Mobile, "1.5.0",
            ("README.md", "# Guide\n\n- [Widgets](widgets.md)\n- [Troubleshooting](troubleshooting.md)"),
            ("widgets.md", "# Pin a widget on the phone\n\nPress and hold the widget."),
            ("troubleshooting.md", "# Troubleshooting\n\nRestart the phone.")),
        Guide(HelpProduct.Mcp, "2.0.0-edge.6",
            ("README.md", "# Guide\n\n- [Connect](connect.md)"),
            ("connect.md", "# Connect an assistant\n\nAdd the server to its configuration.")),
    ]), new Redactor());

    [Theory]
    [InlineData("How do I pin a widget in the app by right-clicking?", "desktop", "Pin-a-widget", "2.0.0")]
    [InlineData("press and hold a widget on the phone", "mobile", "Pin-a-widget-on-the-phone", "1.5.0")]
    [InlineData("connect an assistant", "mcp", "Connect-an-assistant", "2.0.0-edge.6")]
    public void A_question_finds_its_page_and_says_which_product_and_release_it_describes(string query, string product, string page, string release)
    {
        var data = Assert.IsType<HelpSearchData>(Lookup().Read(new(query)).Data);

        var match = data.Matches[0];
        Assert.Equal((product, page, release), (match.Product, match.Page, match.Release));
        Assert.StartsWith($"https://github.com/DashyNMS/{product}/wiki/{page}", match.Link, StringComparison.Ordinal);
        Assert.NotEmpty(match.Passage);
        Assert.Equal(["desktop", "mobile", "mcp"], data.Guides.Select(guide => guide.Product));
    }

    [Fact]
    public void A_question_about_one_product_is_answered_only_from_its_guide()
    {
        var data = Assert.IsType<HelpSearchData>(Lookup().Read(new("pin a widget", Product: "Mobile")).Data);

        Assert.All(data.Matches, match => Assert.Equal("mobile", match.Product));
        Assert.Equal("DashyNMS Mobile", Assert.Single(data.Guides).ProductName);
    }

    [Fact]
    public void Every_guide_with_an_answer_is_heard_before_any_is_heard_twice()
    {
        var data = Assert.IsType<HelpSearchData>(Lookup().Read(new("widget", Limit: 2)).Data);

        Assert.Equal(["desktop", "mobile"], data.Matches.Select(match => match.Product).Order());
    }

    [Fact]
    public void An_unknown_topic_says_so()
    {
        var result = Lookup().Read(new("espresso machine descaling"));

        Assert.Equal(HelpLookup.NoMatch, result.Summary);
        Assert.Empty(Assert.IsType<HelpSearchData>(result.Data).Matches);
    }

    [Fact]
    public void The_limit_is_kept_to_its_range()
    {
        Assert.Single(Assert.IsType<HelpSearchData>(Lookup().Read(new("widget", Limit: 0)).Data).Matches);
        Assert.InRange(Assert.IsType<HelpSearchData>(Lookup().Read(new("widget", Limit: 1000)).Data).Matches.Count, 1, HelpLookup.MaxResults);
    }

    [Fact]
    public void A_page_reads_without_links_that_only_work_in_its_guide()
    {
        var page = Assert.IsType<HelpPageData>(Lookup().Read(new(Page: "Pin a widget", Product: "desktop")).Data);

        Assert.Contains("(picture: A widget)", page.Text, StringComparison.Ordinal);
        Assert.Contains("fixes (page Troubleshooting, section stuck), [the site](https://example.net/a) and gone.", page.Text, StringComparison.Ordinal);
        Assert.Contains("[not a link](troubleshooting.md)", page.Text, StringComparison.Ordinal);
        Assert.Equal("remove-it", Assert.Single(page.Sections).Section);
        Assert.False(page.TextTruncated);
    }

    [Theory]
    [InlineData("remove-it")]
    [InlineData("#remove-it")]
    [InlineData("Remove It")]
    public void A_section_is_read_by_its_anchor_or_heading(string section)
    {
        var page = Assert.IsType<HelpPageData>(Lookup().Read(new(Page: "Pin-a-widget", Section: section)).Data);

        Assert.StartsWith("Choose **Unpin**.", page.Text, StringComparison.Ordinal);
        Assert.Equal("https://github.com/DashyNMS/desktop/wiki/Pin-a-widget#remove-it", page.Link);
    }

    [Fact]
    public void A_long_page_is_cut_and_says_so()
    {
        var page = Assert.IsType<HelpPageData>(Lookup().Read(new(Page: "Long page")).Data);

        Assert.True(page.TextTruncated);
        Assert.InRange(page.Text.Length, 1, HelpLookup.MaxTextLength);
        Assert.Equal("The end.", Assert.IsType<HelpPageData>(Lookup().Read(new(Page: "Long page", Section: "end")).Data).Text);
    }

    [Fact]
    public void A_page_in_more_than_one_guide_asks_which_product()
    {
        var error = Assert.Throws<ToolException>(() => Lookup().Read(new(Page: "Troubleshooting")));

        Assert.Contains("which product", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Details);
        Assert.Equal("mobile", Assert.IsType<HelpPageData>(Lookup().Read(new(Page: "Troubleshooting", Product: "mobile")).Data).Product);
    }

    [Theory]
    [InlineData(null, null, "No such page", null)]
    [InlineData(null, "toaster", null, null)]
    [InlineData(null, "desktop", "Pin a widget", "nowhere")]
    [InlineData(null, null, null, "remove-it")]
    public void What_the_guides_do_not_have_is_a_plain_error(string? query, string? product, string? page, string? section)
        => Assert.Throws<ToolException>(() => Lookup().Read(new(query, product, page, section)));

    [Fact]
    public void Nothing_asked_lists_what_the_guides_hold()
    {
        var contents = Assert.IsType<HelpContentsData>(Lookup().Read(new()).Data).Contents;

        Assert.Equal(3, contents.Count);
        Assert.Equal(new HelpPageBrief("Pin-a-widget", "Pin a widget", "Tasks"), contents[0].Pages[1]);
    }

    [Fact]
    public void Guide_text_goes_through_the_redactor()
    {
        var page = Assert.IsType<HelpPageData>(Lookup().Read(new(Page: "Troubleshooting", Product: "desktop")).Data);
        Assert.DoesNotContain("hunter2secret", page.Text, StringComparison.Ordinal);

        var data = Assert.IsType<HelpSearchData>(Lookup().Read(new("restart the gadget", Product: "desktop")).Data);
        Assert.DoesNotContain("hunter2secret", data.Matches[0].Passage, StringComparison.Ordinal);
    }

    /// <summary>The bundle itself: each guide is there, says its release, and links only to pages it has.</summary>
    [Fact]
    public void The_bundled_guides_are_whole()
    {
        using var library = new HelpLibrary("2.0.0-edge.6");

        Assert.Equal(["desktop", "mobile", "mcp"], library.Guides.Select(guide => guide.Product.Id));
        Assert.All(library.Guides, guide =>
        {
            Assert.NotNull(guide.Home);
            Assert.NotNull(guide.Release);
            Assert.True(guide.Pages.Count > 10, guide.Product.Id);
            Assert.DoesNotContain(guide.Contents, group => group.Name == "More");

            // The redactor leans towards hiding too much, so the guides are worded for it to have nothing to hide.
            var redactor = new Redactor();
            Assert.All(guide.Pages, page => Assert.Equal(page.Markdown, redactor.Redact(page.Markdown)));
        });

        var lookup = new HelpLookup(library, new Redactor());
        Assert.Equal("Arrange-your-dashboard", First("arrange the dashboard cards", "mobile"));
        Assert.Equal("Find-anything-with-Network-Search", First("which switch port is an address on", "desktop"));
        Assert.Equal("Share-one-server-over-HTTP", First("share one server with a team over HTTP", "mcp"));

        string First(string query, string product)
            => Assert.IsType<HelpSearchData>(lookup.Read(new(query, product)).Data).Matches[0].Page;
    }
}
