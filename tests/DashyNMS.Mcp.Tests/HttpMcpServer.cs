using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Tests;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace DashyNMS.Mcp.Tests;

/// <summary>The real server process serving HTTP on a loopback port, with the raw requests a test needs to send it.</summary>
internal sealed class HttpMcpServer : IDisposable
{
    /// <summary>Synthetic tokens of two people, as the server's setting gives them.</summary>
    public const string AliceToken = "synthetic-alice-0123456789abcdef0123456789";
    public const string BobToken = "synthetic-bob-fedcba9876543210fedcba9876543210";

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly HttpClient _http;

    private HttpMcpServer(Process process, Uri root)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
            }
        };
        _process.BeginErrorReadLine();
        Root = root;
        _http = new HttpClient { BaseAddress = root, Timeout = TimeSpan.FromSeconds(60) };
    }

    public Uri Root { get; }

    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Starts the server against <paramref name="librenms"/> and waits until it's listening.</summary>
    public static async Task<HttpMcpServer> StartAsync(FixtureServer librenms, CancellationToken cancellationToken, params (string Key, string Value)[] settings)
    {
        // The port was free a moment ago, but another test can take it first, so try again elsewhere.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartOnceAsync(librenms, cancellationToken, settings);
            }
            catch (InvalidOperationException taken) when (attempt < 5 && taken.Message.Contains("Couldn't listen on", StringComparison.Ordinal))
            {
            }
        }
    }

    private static async Task<HttpMcpServer> StartOnceAsync(FixtureServer librenms, CancellationToken cancellationToken, (string Key, string Value)[] settings)
    {
        var root = FixtureServer.NothingListening();
        var start = StdioServer.StartInfo(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.TransportKey] = "http",
            [McpOptions.HttpPortKey] = root.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [McpOptions.HttpTokensKey] = $"alice:{AliceToken},bob:{BobToken}",
        });
        foreach (var (key, value) in settings)
        {
            start.Environment[key] = value;
        }

        var server = new HttpMcpServer(Process.Start(start)!, root);
        try
        {
            while (true)
            {
                if (server._process.HasExited)
                {
                    // Waiting reads the last of stderr, which says why.
                    await server._process.WaitForExitAsync(cancellationToken);
                    throw new InvalidOperationException($"Server exited. stderr:\n{server.Stderr}");
                }

                try
                {
                    using var answer = await server._http.GetAsync("health", cancellationToken);
                    if (answer.IsSuccessStatusCode && server.Stderr.Contains("Application started", StringComparison.Ordinal))
                    {
                        // It said so itself, so the answer wasn't another test's server on the same port.
                        return server;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? authorization, CancellationToken cancellationToken,
        string? body = null, string? session = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return await _http.SendAsync(request, cancellationToken);
    }

    public Task<HttpResponseMessage> PostAsync(string token, string body, CancellationToken cancellationToken, string? session = null, string? origin = null)
        => SendAsync(HttpMethod.Post, "mcp", "Bearer " + token, cancellationToken, body, session, origin);

    /// <summary>The MCP handshake as a client from before the 2026-07-28 revision makes it. Returns its session.</summary>
    public async Task<string> InitializeAsync(string token, string clientName, CancellationToken cancellationToken)
    {
        using var answer = await PostAsync(
            token,
            $$$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"{{{{clientName}}}}","version":"1"}}}""",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var session = Assert.Single(answer.Headers.GetValues("Mcp-Session-Id"));
        using var ready = await PostAsync(token, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", cancellationToken, session);
        Assert.Equal(HttpStatusCode.Accepted, ready.StatusCode);
        return session;
    }

    /// <summary>Sends a request in a session and returns its response message, which arrives as a server-sent event.</summary>
    public async Task<JsonElement> RequestAsync(string token, string session, int id, string method, string parameters, CancellationToken cancellationToken)
    {
        using var answer = await PostAsync(token, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{parameters}}}}""", cancellationToken, session);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return Message(await answer.Content.ReadAsStringAsync(cancellationToken), id);
    }

    /// <summary>The SDK's own client, as an MCP client built on it connects.</summary>
    public Task<McpClient> ConnectAsync(string token, string clientName, string? protocolVersion, CancellationToken cancellationToken)
        => McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Root, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            }),
            new McpClientOptions { ClientInfo = new() { Name = clientName, Version = "1" }, ProtocolVersion = protocolVersion },
            (ILoggerFactory?)null,
            cancellationToken);

    /// <summary>Waits until the server has recorded <paramref name="count"/> tool calls, and returns them.</summary>
    public async Task<IReadOnlyList<JsonElement>> AuditAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entries = Stderr.Split('\n')
                .Where(line => line.StartsWith("""{"event":"tool_call",""", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToList();
            if (entries.Count >= count)
            {
                return entries;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static JsonElement Message(string body, int id)
    {
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal) && line["data:".Length..].Trim() is { Length: > 0 } data)
            {
                var message = JsonDocument.Parse(data).RootElement;
                if (message.TryGetProperty("id", out var got) && got.GetInt32() == id)
                {
                    return message;
                }
            }
        }

        throw new InvalidOperationException($"No answer to {id} in:\n{body}");
    }

    public void Dispose()
    {
        _http.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }
}
