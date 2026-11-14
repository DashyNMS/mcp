using System.Text.Json;
using DashyNMS.Agent.Graphs;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

namespace DashyNMS.Agent.Tests;

public sealed class SharedGraphLookupTests
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Graphs", "routes.json");
    private static FixtureServer Server(bool denied = false, bool oversized = false)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        var fixture = json.RootElement.Clone();
        var routes = fixture.GetProperty("routes").EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
        return new(path => routes.GetValueOrDefault(path), requestAnswer: request =>
        {
            if (request.QueryString["graph_type"] is not { } format) return null;
            var image = denied ? fixture.GetProperty("deniedSvg").GetString()
                : format == "svg" ? fixture.GetProperty("svg").GetString()
                : oversized ? new string('A', ((IntelligenceImage.MaxBytes + 2) / 3) * 4 + 4) : fixture.GetProperty("png").GetString();
            return new(200, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["status"] = "ok", ["image"] = new Dictionary<string, object?>
                {
                    ["image"] = image, ["content-type"] = denied || format == "svg" ? "image/svg+xml" : "image/png",
                },
            }));
        });
    }
    private static ServiceProvider Connected(Uri root)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(FakeLibreNms.Now));
        var provider = services.AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(root, FixtureServer.Token));
        return provider;
    }
    private static HostGraphLookup Host(ServiceProvider provider) => HostGraphLookup.Create(provider.GetRequiredService<ILibreNmsClient>(),
        provider.GetRequiredService<ILibreNmsTransport>(), provider.GetRequiredService<TimeProvider>());

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("cpu", null, null)]
    [InlineData("errors", "1/1/7", null)]
    [InlineData("temperature", null, "1")]
    public async Task Metadata_images_and_requests_match_MCP_over_HTTP(string? type, string? port, string? sensor)
    {
        using var mcpServer = Server();
        using var hostServer = Server();
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var operation = Host(host);
        var result = (await operation.ReadAsync(new(1, Type: type, Port: port, Sensor: sensor))).Result!;
        var expected = await ActivatorUtilities.CreateInstance<GraphTools>(mcp).Graph("1", type: type, port: port, sensor: sensor);
        var text = FakeLibreNms.Parse(Assert.IsType<TextContentBlock>(expected.Content[0]).Text);
        Assert.Equal(text.GetProperty("summary").GetString(), result.Summary);
        Assert.True(JsonElement.DeepEquals(text.GetProperty("data"), JsonSerializer.SerializeToElement(result.Data, ToolResult.Json)));
        Assert.Equal(1, result.Data.Device.DeviceId);
        Assert.Equal("image/png", result.Image!.MimeType);
        Assert.Equal(Assert.IsType<ImageContentBlock>(expected.Content[1]).DecodedData.ToArray(), result.Image.Data);
        Assert.True(result.Image.Data.Length <= IntelligenceImage.MaxBytes);
        Assert.Equal(mcpServer.HttpRequests.Order(), hostServer.HttpRequests.Order());
        operation.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ReadAsync(new(1)));
    }

    [Fact]
    public async Task Denied_image_returns_refusal_and_no_image()
    {
        using var server = Server(denied: true);
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var result = await host.ReadAsync(new(1));
        Assert.Null(result.Result);
        Assert.Equal(HostFailure.Refused, result.Error);
    }
    [Fact]
    public async Task Oversized_image_returns_failure_before_base64_decoding()
    {
        using var server = Server(oversized: true);
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var result = await host.ReadAsync(new(1));
        Assert.Null(result.Result);
        Assert.Equal(HostFailure.InvalidRequest, result.Error);
    }
}
