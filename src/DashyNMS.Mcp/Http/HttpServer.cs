using System.Net;
using DesktopNMS.Core.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace DashyNMS.Mcp.Http;

/// <summary>
/// The server over MCP's Streamable HTTP transport (#17), so one copy serves
/// a team, behind bearer tokens and a reverse proxy that does TLS.
/// </summary>
internal static class HttpServer
{
    public const string McpPath = "/mcp";
    public const string HealthPath = "/health";
    public const string ReadyPath = "/ready";

    /// <summary>Far more than any tool call needs; results are what's big, and they go the other way.</summary>
    private const long MaxRequestBytes = 256 * 1024;

    private const int MaxConnections = 1000;
    private const int MaxIdleSessions = 200;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(30);

    public static async Task<int> RunAsync(McpOptions options)
    {
        // Empty, so it's only what's set here: the usual builder also reads
        // appsettings.json, the command line and ASPNETCORE_ variables.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.AddDashyNmsLogging();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // Readiness checks would otherwise log a line each.
        builder.Logging.AddFilter(typeof(LibreNmsClient).FullName, LogLevel.Warning);

        builder.WebHost.UseKestrelCore();
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBytes;
            kestrel.Limits.MaxConcurrentConnections = MaxConnections;
            if (options.HttpAddress is { } address)
            {
                kestrel.Listen(address, options.HttpPort);
            }
            else
            {
                kestrel.ListenAnyIP(options.HttpPort);
            }
        });

        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<Readiness>();
        builder.Services.AddDashyNmsServer(options).WithHttpTransport(http =>
        {
            // Clients from before the 2026-07-28 revision say who they are once,
            // when they connect, so they need a session for the audit trail to
            // name them. Newer ones say so with every request and need none.
            http.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
#pragma warning disable MCP9006 // Sessions are deprecated by that revision, not gone: these only bound the older clients'.
            http.IdleTimeout = SessionIdleTimeout;
            http.MaxIdleSessionCount = MaxIdleSessions;
#pragma warning restore MCP9006
        });

        await using var app = builder.Build();
        if (await ServerSetup.ConnectAsync(app.Services, options).ConfigureAwait(false) is { } failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        var gate = new BearerGate(options.HttpTokens!, options.HttpAllowedOrigins);
        app.Use((HttpContext context, RequestDelegate next) => gate.InvokeAsync(context, next));
        string[] probe = [HttpMethods.Get, HttpMethods.Head];
        app.MapMethods(HealthPath, probe, (RequestDelegate)(context => Answer(context, StatusCodes.Status200OK, "ok")));
        app.MapMethods(ReadyPath, probe, (RequestDelegate)ReadyAsync);
        app.MapMcp(McpPath);

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DashyNMS.Mcp");
        log.LogInformation("Serving {Path} on {Address} port {Port} for {Count} tokens", McpPath, options.HttpAddress?.ToString() ?? "every address", options.HttpPort, options.HttpTokens!.Labels.Count());
        try
        {
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (IOException ex)
        {
            // Kestrel couldn't listen: the port is taken, or the address isn't this machine's.
            Console.Error.WriteLine($"Couldn't listen on {McpOptions.HttpAddressKey} and {McpOptions.HttpPortKey}: {ex.Message}");
            return 1;
        }
    }

    private static async Task ReadyAsync(HttpContext context)
    {
        var ready = await context.RequestServices.GetRequiredService<Readiness>().IsReadyAsync(context.RequestAborted).ConfigureAwait(false);
        await (ready ? Answer(context, StatusCodes.Status200OK, "ready") : Answer(context, StatusCodes.Status503ServiceUnavailable, "not ready")).ConfigureAwait(false);
    }

    /// <summary>A probe's whole answer: a status and a word, with nothing about the network or the server in it.</summary>
    private static async Task Answer(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
