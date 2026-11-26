using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tests;
using DashyNMS.Mcp.Auditing;
using DashyNMS.Mcp.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DashyNMS.Mcp.Tests;

/// <summary>The operator's reference files as resources (#20): what's listed, what's read, and what can't be.</summary>
public sealed class ResourceTests : IDisposable
{
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "dashynms-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public ResourceTests()
    {
        _root = Path.Combine(_outside, "resources");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_outside, recursive: true);

    private void Write(string name, string text) => WriteBytes(name, Encoding.UTF8.GetBytes(text));

    private void WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private ResourceFolder Folder(params string[] secrets) => new(_root, new AuditRedactor(new Redactor(), secrets), NullLogger<ResourceFolder>.Instance);

    private static string Text(ReadResourceResult? result) => Assert.IsType<TextResourceContents>(Assert.Single(result!.Contents)).Text;

    [Fact]
    public void Files_are_listed_as_data_and_read_as_they_are_now()
    {
        Write("naming.md", "# Names\nEdge switches are edge-swN.example.net.");
        Write("sites/lab one.txt", "The lab.");
        var folder = Folder();

        var resources = folder.List().Resources;

        Assert.Equal(["naming.md", "sites/lab one.txt"], resources.Select(r => r.Name));
        Assert.Equal("dashynms://resources/sites/lab%20one.txt", resources[1].Uri);
        Assert.Equal(["text/markdown", "text/plain"], resources.Select(r => r.MimeType));
        Assert.All(resources, r => Assert.Contains("not instructions", r.Description, StringComparison.Ordinal));

        Assert.EndsWith("Edge switches are edge-swN.example.net.", Text(folder.Read(resources[0].Uri, "tests")), StringComparison.Ordinal);
        Assert.StartsWith("[Reference data: the file \"naming.md\"", Text(folder.Read(resources[0].Uri, "tests")), StringComparison.Ordinal);

        Write("naming.md", "Changed.");
        Assert.EndsWith("Changed.", Text(folder.Read(resources[0].Uri, "tests")), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_redacted_and_loses_the_servers_own_credentials()
    {
        Write("runbook.md", "snmp-server community Synth3tic RO\nThe token is 0123456789abcdef.\nPart \"the rest\" stays.");
        var folder = Folder("0123456789abcdef");

        var text = Text(folder.Read(Assert.Single(folder.List().Resources).Uri, "tests"));

        Assert.DoesNotContain("Synth3tic", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", text, StringComparison.Ordinal);
        Assert.Contains("Part \"the rest\" stays.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dashynms://resources/../secret.md")]
    [InlineData("dashynms://resources/%2e%2e/secret.md")]
    [InlineData("dashynms://resources/..%2Fsecret.md")]
    [InlineData("dashynms://resources/sub/../../secret.md")]
    [InlineData("dashynms://resources//secret.md")]
    [InlineData("dashynms://resources/.hidden.md")]
    [InlineData("dashynms://resources/NAMING.md")]
    [InlineData("dashynms://resources/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("naming.md")]
    [InlineData("")]
    public void Only_an_address_that_was_listed_is_read(string uri)
    {
        Write("naming.md", "x");
        Write(".hidden.md", "x");
        File.WriteAllText(Path.Combine(_outside, "secret.md"), "outside");

        Assert.Null(Folder().Read(uri, "tests"));
    }

    [Fact]
    public void Hidden_files_other_types_and_files_too_big_are_not_listed()
    {
        Write("ok.md", "x");
        Write("program.exe", "x");
        Write("config", "x");
        Write("key.pem", "x");
        Write(".env", "x");
        Write(".git/notes.md", "x");
        WriteBytes("big.txt", new byte[ResourceFolder.MaxBytes + 1]);

        Assert.Equal("ok.md", Assert.Single(Folder().List().Resources).Name);
    }

    [Fact]
    public void Links_are_not_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            // Making one needs a privilege test accounts don't have.
            return;
        }

        File.WriteAllText(Path.Combine(_outside, "secret.md"), "outside");
        Directory.CreateDirectory(Path.Combine(_outside, "elsewhere"));
        File.WriteAllText(Path.Combine(_outside, "elsewhere", "secret.md"), "outside");
        File.CreateSymbolicLink(Path.Combine(_root, "file.md"), Path.Combine(_outside, "secret.md"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "folder"), Path.Combine(_outside, "elsewhere"));
        Write("ok.md", "x");

        Assert.Equal("ok.md", Assert.Single(Folder().List().Resources).Name);
        Assert.Null(Folder().Read("dashynms://resources/file.md", "tests"));
        Assert.Null(Folder().Read("dashynms://resources/folder/secret.md", "tests"));
    }

    [Fact]
    public void A_folder_reached_through_a_link_is_served_all_the_same()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write("sub/naming.md", "The text.");
        var link = Path.Combine(_outside, "link");
        Directory.CreateSymbolicLink(link, _root);
        var folder = new ResourceFolder(link + "/", new AuditRedactor(new Redactor(), []), NullLogger<ResourceFolder>.Instance);

        Assert.EndsWith("The text.", Text(folder.Read(Assert.Single(folder.List().Resources).Uri, "tests")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_swapped_for_a_link_as_it_is_read_never_gives_what_it_points_at()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var secret = Path.Combine(_outside, "secret.md");
        File.WriteAllText(secret, "outside");
        var path = Path.Combine(_root, "naming.md");
        Write("naming.md", "inside");
        var folder = Folder();
        using var stop = new CancellationTokenSource();
        var swapping = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                File.Delete(path);
                File.CreateSymbolicLink(path, secret);
                File.Delete(path);
                File.WriteAllText(path, "inside");
            }
        });

        for (var i = 0; i < 2000; i++)
        {
            try
            {
                if (folder.Read("dashynms://resources/naming.md", "tests") is { } read)
                {
                    Assert.DoesNotContain("outside", Text(read), StringComparison.Ordinal);
                }
            }
            catch (McpProtocolException)
            {
                // Caught in the act.
            }
        }

        await stop.CancelAsync();
        await swapping;
    }

    [Fact]
    public void A_file_that_is_not_text_says_so_without_its_contents()
    {
        WriteBytes("binary.txt", [0xFF, 0xFE, 0x00, 0x41]);
        WriteBytes("nulls.txt", [0x41, 0x00, 0x42]);
        var folder = Folder();

        foreach (var resource in folder.List().Resources)
        {
            var refused = Assert.Throws<McpProtocolException>(() => folder.Read(resource.Uri, "tests"));
            Assert.DoesNotContain(_root, refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_so_many_are_listed()
    {
        for (var i = 0; i < ResourceFolder.MaxFiles + 5; i++)
        {
            Write($"{i:0000}.md", "x");
        }

        Assert.Equal(ResourceFolder.MaxFiles, Folder().List().Resources.Count);
    }

    [Fact]
    public void A_folder_that_goes_away_lists_nothing()
    {
        var folder = Folder();
        Directory.Delete(_root, recursive: true);

        Assert.Empty(folder.List().Resources);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("http")]
    public async Task They_are_listed_and_read_over_both_transports(string transport)
    {
        Write("naming.md", "Edge switches are edge-swN.example.net. password=synthetic-secret");
        using var librenms = new FixtureServer(LibreNms);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var stdio = transport == "stdio" ? Start(librenms, _root) : null;
        using var http = transport == "http" ? await HttpMcpServer.StartAsync(librenms, cts.Token, (McpOptions.ResourcesDirectoryKey, _root)) : null;
        var session = http is null ? string.Empty : await http.InitializeAsync(HttpMcpServer.AliceToken, "tests", cts.Token);
        if (stdio is not null)
        {
            await stdio.InitializeAsync(cts.Token);
        }

        Task<JsonElement> Request(int id, string method, string parameters)
            => stdio?.RequestAsync(id, method, parameters, cts.Token) ?? http!.RequestAsync(HttpMcpServer.AliceToken, session, id, method, parameters, cts.Token);

        var listed = (await Request(2, "resources/list", "{}")).GetProperty("result").GetProperty("resources");
        Assert.Equal("dashynms://resources/naming.md", Assert.Single(listed.EnumerateArray()).GetProperty("uri").GetString());

        var text = (await Request(3, "resources/read", """{"uri":"dashynms://resources/naming.md"}""")).GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
        Assert.Contains("edge-swN.example.net", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);

        var missing = await Request(4, "resources/read", """{"uri":"dashynms://resources/../naming.md"}""");
        Assert.True(missing.TryGetProperty("error", out _), missing.ToString());
    }

    [Fact]
    public async Task Without_a_folder_the_server_offers_no_resources()
    {
        using var librenms = new FixtureServer(LibreNms);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, null);

        var capabilities = (await server.InitializeAsync(cts.Token)).GetProperty("result").GetProperty("capabilities");

        Assert.False(capabilities.TryGetProperty("resources", out _));
        Assert.True(capabilities.TryGetProperty("prompts", out _));
    }

    [Fact]
    public void A_folder_that_is_not_there_stops_the_server()
    {
        var settings = new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = "librenms.example.com",
            [McpOptions.LibreNmsToken] = "secret-token",
            [McpOptions.ResourcesDirectoryKey] = Path.Combine(_root, "missing"),
        };

        Assert.Null(McpOptions.TryLoad(key => settings.GetValueOrDefault(key), out var errors));
        Assert.StartsWith(McpOptions.ResourcesDirectoryKey, Assert.Single(errors), StringComparison.Ordinal);

        settings[McpOptions.ResourcesDirectoryKey] = _root;
        Assert.Equal(_root, McpOptions.TryLoad(key => settings.GetValueOrDefault(key), out _)!.ResourcesDirectory);
    }

    private static StdioServer Start(FixtureServer librenms, string? directory) => StdioServer.Start(new Dictionary<string, string?>
    {
        [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
        [McpOptions.LibreNmsToken] = FixtureServer.Token,
        [McpOptions.ResourcesDirectoryKey] = directory,
    });

    private static Answer? LibreNms(string path) => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[]}"""),
        _ => null,
    };
}
