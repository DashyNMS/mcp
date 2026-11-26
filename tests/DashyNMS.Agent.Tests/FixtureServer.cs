using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DashyNMS.Agent.Tests;

internal sealed record Answer(int Status, string Body)
{
    /// <summary>Accept the request and never answer it.</summary>
    public static readonly Answer Hang = new(0, "");
}

/// <summary>
/// A loopback LibreNMS answering from the fixture, recording each request's path under
/// <c>/api/v0/</c>. Requests without the connection's token are refused, as LibreNMS would.
/// </summary>
internal sealed class FixtureServer : IDisposable
{
    /// <summary>The token every fixture connection uses; any other is refused.</summary>
    internal const string Token = "test-token";

    private readonly HttpListener listener;
    private readonly ConcurrentQueue<string> requests = new();
    private readonly Func<string, Answer?> answer;
    private readonly Func<HttpListenerRequest, Answer?>? requestAnswer;
    private readonly Func<HttpListenerRequest, bool>? authenticated;
    private readonly ConcurrentQueue<string> httpRequests = new();
    private readonly CancellationTokenSource stopping = new();

    public FixtureServer(Func<string, Answer?> answer, Func<HttpListenerRequest, Answer?>? requestAnswer = null, Func<HttpListenerRequest, bool>? authenticated = null)
    {
        this.answer = answer;
        this.requestAnswer = requestAnswer;
        this.authenticated = authenticated;
        (listener, Root) = Listen();
        _ = Task.Run(ServeAsync);
    }

    public Uri Root { get; }

    /// <summary>
    /// A listener on a loopback port. HttpListener can't choose one itself, so a
    /// free one is picked first; tests run in parallel, and another can take it
    /// in between, so it's tried again elsewhere.
    /// </summary>
    internal static (HttpListener Listener, Uri Root) Listen()
    {
        for (var attempt = 1; ; attempt++)
        {
            var root = NothingListening();
            var candidate = new HttpListener();
            candidate.Prefixes.Add(root.ToString());
            try
            {
                candidate.Start();
                return (candidate, root);
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                candidate.Close();
            }
        }
    }

    /// <summary>A loopback address that was free a moment ago, so a connection is refused.</summary>
    internal static Uri NothingListening()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    public string[] Requests => requests.ToArray();
    public string[] HttpRequests => httpRequests.ToArray();

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        const string api = "/api/v0/";
        var relative = path.StartsWith(api, StringComparison.Ordinal) ? path[api.Length..] : path;
        requests.Enqueue(relative);
        httpRequests.Enqueue(context.Request.Url!.PathAndQuery);
        var reply = (authenticated?.Invoke(context.Request) ?? context.Request.Headers["X-Auth-Token"] == Token)
            ? requestAnswer?.Invoke(context.Request) ?? answer(relative) ?? new Answer(404, """{"status":"error","message":"No fixture for this path"}""")
            : new Answer(401, """{"status":"error","message":"Unauthenticated."}""");
        try
        {
            if (reply == Answer.Hang)
            {
                await Task.Delay(Timeout.Infinite, stopping.Token);
                return;
            }

            var body = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.Body.StartsWith('<') ? "text/html" : "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client gave up (a timeout) or the server is stopping.
        }
    }

    public void Dispose()
    {
        stopping.Cancel();

        // Not Stop first: Close would then look the port up again, and listen on
        // it to do so, which fails if another test has taken it in between.
        listener.Close();
    }
}
