using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Requests;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Agent.Tests;

public sealed class ToolRunnerTests
{
    private readonly ToolRunner _runner = new(new Redactor(), new AgentOptions(), NullLogger<ToolRunner>.Instance);

    private async Task<JsonElement> Run(Func<CancellationToken, Task<JsonObject>> body, CancellationToken cancellationToken = default)
        => JsonDocument.Parse(await _runner.RunAsync("test_tool", body, cancellationToken)).RootElement;

    private static Func<CancellationToken, Task<JsonObject>> Throws(Exception ex) => _ => Task.FromException<JsonObject>(ex);

    /// <summary>How a host that asked is told each kind of call went (#39).</summary>
    [Fact]
    public async Task A_call_reports_how_it_went()
    {
        static Task<Fetched<int>> Source(Exception? error) => Sources.TryAsync(() => error is null ? Task.FromResult(1) : Task.FromException<int>(error), CancellationToken.None);
        async Task<ToolCallOutcome> Report(Exception? thrown, params Exception?[] sources)
        {
            var report = ToolCallReport.Begin();
            var json = await _runner.RunAsync("test_tool", async _ =>
            {
                foreach (var source in sources)
                {
                    await Source(source);
                }

                return thrown is null ? ToolResult.Ok("Fine.", new { up = true }) : throw thrown;
            }, CancellationToken.None);
            Assert.Equal(json, report.Json);
            return report.Outcome;
        }

        var down = new LibreNmsApiException("down", (HttpStatusCode?)null);
        var broken = new LibreNmsApiException("broken", HttpStatusCode.BadGateway);
        var denied = new LibreNmsApiException("denied", HttpStatusCode.Forbidden);
        var refused = new LibreNmsApiException("refused", HttpStatusCode.Unauthorized);
        var missing = new LibreNmsApiException("missing", HttpStatusCode.NotFound);

        Assert.Equal(ToolCallOutcome.Complete, await Report(null));
        Assert.Equal(ToolCallOutcome.Complete, await Report(null, null, null));
        Assert.Equal(ToolCallOutcome.Partial, await Report(null, null, down));
        Assert.Equal(ToolCallOutcome.Partial, await Report(null, null, missing));
        Assert.Equal(ToolCallOutcome.Unreachable, await Report(null, down, broken));
        Assert.Equal(ToolCallOutcome.Unreachable, await Report(down));
        Assert.Equal(ToolCallOutcome.Unreachable, await Report(new TaskCanceledException()));
        Assert.Equal(ToolCallOutcome.Unreachable, await Report(new ToolException("It couldn't be read."), broken));
        Assert.Equal(ToolCallOutcome.Failed, await Report(new ToolException("No device matches.")));
        Assert.Equal(ToolCallOutcome.Failed, await Report(new ToolException("No device matches."), [null]));
        Assert.Equal(ToolCallOutcome.Failed, await Report(missing));
        Assert.Equal(ToolCallOutcome.Failed, await Report(new InvalidOperationException()));
        // What this account has never been allowed isn't missing; what it needs and is refused is.
        Assert.Equal(ToolCallOutcome.Complete, await Report(null, null, denied));
        Assert.Equal(ToolCallOutcome.Denied, await Report(null, denied, down));
        Assert.Equal(ToolCallOutcome.Denied, await Report(denied));
        Assert.Equal(ToolCallOutcome.TokenRefused, await Report(null, null, refused));
        Assert.Equal(ToolCallOutcome.TokenRefused, await Report(refused));
    }

    [Fact]
    public async Task Success_is_rendered()
    {
        var root = await Run(_ => Task.FromResult(ToolResult.Ok("Fine.", new { up = true })));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("data").GetProperty("up").GetBoolean());
    }

    [Fact]
    public async Task Tool_exceptions_keep_their_message_and_details()
    {
        var root = await Run(Throws(new ToolException("2 devices match.", new { candidates = new[] { "a", "b" } })));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal("2 devices match.", root.GetProperty("message").GetString());
        Assert.Equal(2, root.GetProperty("details").GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task LibreNms_failures_use_the_user_message()
    {
        var root = await Run(Throws(new LibreNmsApiException("raw", HttpStatusCode.Forbidden)));

        Assert.Equal($"LibreNMS: {LibreNmsApiException.PermissionDeniedMessage}", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unexpected_failures_hide_the_detail()
    {
        var root = await Run(Throws(new InvalidOperationException("at Foo.Bar() in C:\\src\\secret.cs token=abc")));

        var message = root.GetProperty("message").GetString();
        Assert.Equal("Something went wrong running test_tool. The server log has the details.", message);
    }

    [Fact]
    public async Task A_timeout_is_not_mistaken_for_the_callers_cancellation()
    {
        var root = await Run(Throws(new TaskCanceledException()));

        Assert.StartsWith("A request timed out.", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_callers_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync("test_tool", ct => Task.FromCanceled<JsonObject>(ct), cts.Token));
    }

    [Fact]
    public async Task Error_text_is_redacted_too()
    {
        var root = await Run(Throws(new LibreNmsApiException("raw", HttpStatusCode.BadRequest, serverMessage: "bad value password=hunter2")));

        Assert.DoesNotContain("hunter2", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
