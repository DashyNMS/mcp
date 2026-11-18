using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The real server process, spoken to over stdio as an MCP client would, so a
/// test covers the environment variables, startup and the JSON a client sees
/// rather than an in-process shortcut.
/// </summary>
internal sealed class StdioServer : IDisposable
{
    /// <summary>
    /// Prefixes of every setting the server reads. They're cleared from the
    /// inherited environment so a developer's own configuration can't leak into
    /// a test and quietly change what it proves.
    /// </summary>
    private static readonly string[] SettingPrefixes = ["LIBRENMS_", "UNIMUS_", "GRAYLOG_", "MCP_"];

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private StdioServer(Process process)
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
    }

    /// <summary>Everything the server has written to stderr so far.</summary>
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

    /// <summary>Starts the server with only <paramref name="settings"/> configured; a null value leaves that setting unset.</summary>
    public static StdioServer Start(IReadOnlyDictionary<string, string?> settings)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "dashynms-mcp.dll")}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var key in start.Environment.Keys.Where(k => SettingPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList())
        {
            start.Environment.Remove(key);
        }

        foreach (var (key, value) in settings)
        {
            if (value is not null)
            {
                start.Environment[key] = value;
            }
        }

        return new StdioServer(Process.Start(start)!);
    }

    /// <summary>Sends a request and returns the response with the same ID, skipping any notifications in between.</summary>
    public async Task<JsonElement> RequestAsync(int id, string method, string parameters, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
        await _process.StandardInput.FlushAsync(cancellationToken);
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Server exited. stderr:\n{Stderr}");
            var message = JsonDocument.Parse(line).RootElement;
            if (message.TryGetProperty("id", out var got) && got.GetInt32() == id)
            {
                return message;
            }
        }
    }

    /// <summary>The MCP handshake: initialise, then tell the server the client is ready. Returns the initialise response.</summary>
    public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken)
    {
        var init = await RequestAsync(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tests","version":"1"}}""", cancellationToken);
        await _process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await _process.StandardInput.FlushAsync(cancellationToken);
        return init;
    }

    /// <summary>Calls a tool and returns the JSON document in its first text block, as a model would read it.</summary>
    public async Task<JsonElement> CallToolAsync(int id, string name, string arguments, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(id, "tools/call", $$"""{"name":"{{name}}","arguments":{{arguments}}}""", cancellationToken);
        var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }
}
