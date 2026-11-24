using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Mcp.Auditing;

/// <summary>
/// Sends audit entries to Graylog over GELF too (#18), so the trail sits with
/// the rest of the network's logs: a GELF HTTP input, or a GELF UDP one.
/// </summary>
/// <remarks>
/// In the background and best effort. A Graylog that's slow or down must not
/// slow a tool call or stop it, so entries wait in a bounded queue and the
/// oldest are dropped when it's full. stderr has every entry whatever happens
/// here, and says when delivery fails.
/// </remarks>
public sealed class GelfAuditSink : BackgroundService, IAuditSink
{
    internal const int QueueLength = 1000;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long entries still queued at shutdown are given to go.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

    private readonly GelfTarget _target;
    private readonly ILogger<GelfAuditSink> _logger;
    private readonly Channel<AuditEntry> _queue;
    private readonly HttpClient? _http;
    private readonly Socket? _udp;
    private long _lost;
    private bool _failing;

    public GelfAuditSink(GelfTarget target, ILogger<GelfAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _logger = logger;
        _queue = Channel.CreateBounded<AuditEntry>(
            new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _lost));
        if (target.IsUdp)
        {
            // Dual-stack wherever the machine has IPv6.
            _udp = new Socket(SocketType.Dgram, ProtocolType.Udp);
        }
        else
        {
            _http = CreateHttp(target);
        }
    }

    public void Write(AuditEntry entry)
    {
        // Never waits: full means the oldest is dropped, which the queue counts.
        _queue.Writer.TryWrite(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await DeliverAsync(entry, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Out of time at shutdown; StopAsync says what's left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // No more come, so the loop ends by itself once what's queued has gone.
        _queue.Writer.TryComplete();
        if (ExecuteTask is { } sending)
        {
            await Task.WhenAny(sending, Task.Delay(FlushTimeout, cancellationToken)).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        var left = _queue.Reader.Count + Interlocked.Exchange(ref _lost, 0);
        if (left > 0)
        {
            _logger.LogWarning("{Count} audit entries weren't sent to Graylog. They're all in this log.", left);
        }
    }

    private async Task DeliverAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(Message(entry, Environment.MachineName), cancellationToken).ConfigureAwait(false);
            if (_failing)
            {
                _failing = false;
                _logger.LogInformation("Audit entries are reaching Graylog again; {Count} weren't sent. They're all in this log.", Interlocked.Exchange(ref _lost, 0));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _lost);
            throw;
        }
        catch (Exception ex)
        {
            // Anything: an exception out of here would stop the whole server.
            Interlocked.Increment(ref _lost);
            if (!_failing)
            {
                // Once, not for every entry. The failure's kind only: its text can hold the address.
                _failing = true;
                _logger.LogWarning("Audit entries aren't reaching Graylog ({Reason}). They're still all in this log.", Describe(ex));
            }
        }
    }

    private async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        if (_udp is not null)
        {
            var addresses = await Dns.GetHostAddressesAsync(_target.Address.DnsSafeHost, timeout.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork || _udp.AddressFamily == AddressFamily.InterNetworkV6)
                ?? throw new SocketException((int)SocketError.HostNotFound);
            await _udp.SendToAsync(message, SocketFlags.None, new IPEndPoint(address, _target.Address.Port), timeout.Token).ConfigureAwait(false);
            return;
        }

        using var content = new ByteArrayContent(message);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http!.PostAsync(_target.Address, content, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "its certificate isn't trusted; see AUDIT_GELF_TRUSTED_CERTS",
        HttpRequestException http => http.HttpRequestError.ToString(),
        SocketException socket => socket.SocketErrorCode.ToString(),
        OperationCanceledException => "timed out",
        _ => ex.GetType().Name,
    };

    private static HttpClient CreateHttp(GelfTarget target)
    {
        // No redirects: the trail goes where the operator said and nowhere else.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        handler.SslOptions.RemoteCertificateValidationCallback = CertificateTrust.CreateCallback(
            target.Address.Host, allowUntrusted: true, target.TrustedCertificates, _ => { });
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The entry as a GELF 1.1 message, its own fields with GELF's leading underscore.</summary>
    internal static byte[] Message(AuditEntry entry, string host)
    {
        var message = new JsonObject
        {
            ["version"] = "1.1",
            ["host"] = host,
            ["short_message"] = $"MCP tool call {entry.Tool}: {entry.Status}",
            ["timestamp"] = Math.Round(entry.Time.ToUnixTimeMilliseconds() / 1000d, 3),
            ["level"] = entry.Status is AuditStatus.Ok or AuditStatus.Cancelled ? 6 : 4,
            ["_event"] = "tool_call",
            ["_transport"] = entry.Transport,
            ["_tool"] = entry.Tool,
            ["_status"] = entry.Status,
            ["_result_bytes"] = entry.ResultBytes,
            ["_duration_ms"] = (long)entry.Duration.TotalMilliseconds,
        };

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                message[name] = value;
            }
        }

        Add("_token_label", entry.TokenLabel);
        Add("_client_name", entry.ClientName);
        Add("_client_version", entry.ClientVersion);

        // GELF fields are text or numbers, so the arguments go as their JSON.
        Add("_arguments", entry.Arguments?.ToJsonString());
        if (entry.UndeclaredArguments > 0)
        {
            message["_undeclared_arguments"] = entry.UndeclaredArguments;
        }

        return Encoding.UTF8.GetBytes(message.ToJsonString());
    }

    public override void Dispose()
    {
        _http?.Dispose();
        _udp?.Dispose();
        base.Dispose();
    }
}
