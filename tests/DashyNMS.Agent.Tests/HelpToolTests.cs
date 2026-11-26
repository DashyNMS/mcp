using System.Net;
using System.Text;
using DashyNMS.Agent.Help;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Help;

namespace DashyNMS.Agent.Tests;

/// <summary>The help tool's JSON (#164): its three shapes against its output schema, and its errors.</summary>
public sealed class HelpToolTests
{
    private static HelpLibrary Library() => new(
    [
        HelpGuide.Load(HelpProduct.Desktop, HelpRelease.ForVersion("2.0.0"), new HelpFileSet(new Dictionary<string, byte[]>
        {
            ["docs/guide/README.md"] = "# Guide\n\n- [Widgets](widgets.md)"u8.ToArray(),
            ["docs/guide/widgets.md"] = Encoding.UTF8.GetBytes("# Pin a widget\n\nRight-click the widget.\n\n## Remove it\n\n" + string.Concat(Enumerable.Repeat("Choose Unpin, then wait. ", 200))),
        })),
    ]);

    /// <summary>No LibreNMS client anywhere: the runner checks each result against the tool's schema.</summary>
    private static HelpTools Tools(AgentOptions? options = null)
        => new(Library(), new FakeLibreNms(options).Runner);

    [Fact]
    public async Task A_search_gives_marked_passages_and_links()
    {
        var root = FakeLibreNms.Parse(await Tools().DashynmsHelp("how do I pin a widget"));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        var match = root.GetProperty("data").GetProperty("matches")[0];
        Assert.Equal("desktop", match.GetProperty("product").GetString());
        Assert.Equal("2.0.0", match.GetProperty("release").GetString());
        Assert.Equal("https://github.com/DashyNMS/desktop/wiki/Pin-a-widget", match.GetProperty("link").GetString());
    }

    [Fact]
    public async Task A_page_and_the_contents_fit_the_schema_too()
    {
        var page = FakeLibreNms.Parse(await Tools().DashynmsHelp(page: "Pin-a-widget", section: "remove-it")).GetProperty("data");
        Assert.Equal("Remove it", page.GetProperty("heading").GetString());
        Assert.False(page.GetProperty("text_truncated").GetBoolean());

        var contents = FakeLibreNms.Parse(await Tools().DashynmsHelp()).GetProperty("data").GetProperty("contents");
        Assert.Equal("Pin-a-widget", contents[0].GetProperty("pages")[1].GetProperty("page").GetString());
    }

    [Fact]
    public async Task Nothing_found_is_an_answer_and_a_wrong_name_is_an_error()
    {
        var none = FakeLibreNms.Parse(await Tools().DashynmsHelp("espresso machine descaling"));
        Assert.Equal("ok", none.GetProperty("status").GetString());
        Assert.Equal(HelpLookup.NoMatch, none.GetProperty("summary").GetString());
        Assert.Empty(none.GetProperty("data").GetProperty("matches").EnumerateArray());

        var wrong = FakeLibreNms.Parse(await Tools().DashynmsHelp(product: "toaster"));
        Assert.Equal("error", wrong.GetProperty("status").GetString());
        Assert.DoesNotContain("toaster", wrong.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_small_cap_still_fits_the_schema()
    {
        var root = FakeLibreNms.Parse(await Tools(new AgentOptions { MaxResultBytes = 1024 }).DashynmsHelp(page: "Pin-a-widget"));

        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Nothing_is_requested_unless_refreshing_is_on()
    {
        var server = new CountingHandler();
        using var off = new HelpLibrary("2.0.0", refresh: false, new HttpClient(server));
        await new HelpTools(off, new FakeLibreNms().Runner).DashynmsHelp("pin a sensor");
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Refreshing_never_holds_up_an_answer_and_sends_no_question()
    {
        var server = new CountingHandler();
        using var on = new HelpLibrary("2.0.0", refresh: true, new HttpClient(server));

        var root = FakeLibreNms.Parse(await new HelpTools(on, new FakeLibreNms().Runner).DashynmsHelp("pin a sensor to my secret dashboard"));
        Assert.Equal("ok", root.GetProperty("status").GetString());

        // Three guides, each asked for its front page once; none is published here, so nothing more.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (server.Requests < 3)
        {
            await Task.Delay(20, limit.Token);
        }

        Assert.All(server.Addresses, address =>
        {
            Assert.StartsWith("https://raw.githubusercontent.com/DashyNMS/", address, StringComparison.Ordinal);
            Assert.EndsWith("/docs/guide/README.md", address, StringComparison.Ordinal);
        });
        Assert.Equal(3, on.Guides.Count);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly List<string> _addresses = [];

        public int Requests
        {
            get
            {
                lock (_addresses)
                {
                    return _addresses.Count;
                }
            }
        }

        public IReadOnlyList<string> Addresses
        {
            get
            {
                lock (_addresses)
                {
                    return _addresses.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_addresses)
            {
                _addresses.Add(request.RequestUri!.ToString());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }
}
