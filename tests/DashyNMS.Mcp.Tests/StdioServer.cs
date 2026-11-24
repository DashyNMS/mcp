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

    /// <summary>
    /// Everything the server has written to stderr so far, for diagnostics
    /// while it runs. Lines arrive asynchronously, so a check that nothing was
    /// written uses <see cref="StopAsync"/> instead.
    /// </summary>
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

    /// <summary>Sends one message as it is, without waiting for anything.</summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(message);
        await _process.StandardInput.FlushAsync(cancellationToken);
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

    /// <summary>
    /// Sends every call without waiting for the answers, as an agent calling
    /// tools in parallel does, and returns each call's JSON document by ID in
    /// whatever order the answers come.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, JsonElement>> CallToolsAsync(IReadOnlyList<(int Id, string Name, string Arguments)> calls, CancellationToken cancellationToken)
    {
        foreach (var (id, name, arguments) in calls)
        {
            await _process.StandardInput.WriteLineAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{arguments}}}}}""");
        }

        await _process.StandardInput.FlushAsync(cancellationToken);
        var results = new Dictionary<int, JsonElement>();
        while (results.Count < calls.Count)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Server exited. stderr:\n{Stderr}");
            var message = JsonDocument.Parse(line).RootElement;
            if (message.TryGetProperty("id", out var id))
            {
                var text = message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
                results.Add(id.GetInt32(), JsonDocument.Parse(text).RootElement);
            }
        }

        return results;
    }

    /// <summary>
    /// Waits for a server that refused to start to exit, returning its exit
    /// code and whatever it wrote to stdout, which must stay empty: an MCP
    /// client would read anything there as protocol.
    /// </summary>
    public async Task<(int ExitCode, string Stdout)> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var stdout = await _process.StandardOutput.ReadToEndAsync(cancellationToken);
        await _process.WaitForExitAsync(cancellationToken);
        return (_process.ExitCode, stdout);
    }

    /// <summary>
    /// Ends the session as a client does, by closing stdin, and waits for the
    /// process to exit, which also waits for the last stderr line to be read.
    /// A server that doesn't exit within ten seconds is killed, then drained
    /// the same way. Returns the complete stderr.
    /// </summary>
    public async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        _process.StandardInput.Close();
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }

        return Stderr;
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
