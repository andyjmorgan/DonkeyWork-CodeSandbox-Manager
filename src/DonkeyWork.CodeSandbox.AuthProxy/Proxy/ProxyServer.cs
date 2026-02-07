using System.Net;
using System.Net.Sockets;
using System.Text;
using DonkeyWork.CodeSandbox.AuthProxy.Configuration;

namespace DonkeyWork.CodeSandbox.AuthProxy.Proxy;

public class ProxyServer : BackgroundService
{
    private readonly ProxyConfiguration _config;
    private readonly TlsMitmHandler _mitmHandler;
    private readonly ILogger<ProxyServer> _logger;
    private readonly HashSet<string> _allowedDomains;

    public ProxyServer(
        ProxyConfiguration config,
        TlsMitmHandler mitmHandler,
        ILogger<ProxyServer> logger)
    {
        _config = config;
        _mitmHandler = mitmHandler;
        _logger = logger;
        _allowedDomains = new HashSet<string>(config.AllowedDomains, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _config.ProxyPort);
        listener.Start();

        _logger.LogInformation("Proxy server listening on port {Port}", _config.ProxyPort);
        _logger.LogInformation("Allowed domains: {Domains}", string.Join(", ", _allowedDomains));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                // Read the CONNECT request
                var requestLine = await ReadHttpRequestLineAsync(stream, cancellationToken);
                if (requestLine == null)
                {
                    return;
                }

                // Parse CONNECT host:port
                var (method, host, port) = ParseConnectRequest(requestLine);
                if (method == null || !method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Non-CONNECT method received: {Method}", method);
                    await SendResponseAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\n\r\n", cancellationToken);
                    return;
                }

                if (host == null)
                {
                    _logger.LogWarning("Invalid CONNECT request: {RequestLine}", requestLine);
                    await SendResponseAsync(stream, "HTTP/1.1 400 Bad Request\r\n\r\n", cancellationToken);
                    return;
                }

                // Consume remaining headers
                await ReadRemainingHeadersAsync(stream, cancellationToken);

                // Check domain allowlist
                if (!IsDomainAllowed(host))
                {
                    _logger.LogWarning("CONNECT request: {Host}:{Port} - BLOCKED (not in allowlist)", host, port);
                    await SendResponseAsync(stream,
                        "HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\n\r\nDomain not in allowlist\r\n",
                        cancellationToken);
                    return;
                }

                _logger.LogInformation("CONNECT request: {Host}:{Port} - ALLOWED (MITM mode)", host, port);

                // Accept the CONNECT tunnel
                await SendResponseAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", cancellationToken);

                // Hand off to MITM handler
                await _mitmHandler.HandleMitmConnectionAsync(stream, host, port, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _logger.LogDebug("Client connection closed: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client connection");
            }
        }
    }

    private async Task<string?> ReadHttpRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();

        // Read until we get the full request line (terminated by \r\n)
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                return null;

            sb.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

            var data = sb.ToString();
            var lineEnd = data.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd >= 0)
            {
                return data[..lineEnd];
            }

            if (sb.Length > 4096)
            {
                _logger.LogWarning("Request line too long, aborting");
                return null;
            }
        }
    }

    private async Task ReadRemainingHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Read until we find the empty line (\r\n\r\n) marking end of headers
        var buffer = new byte[4096];
        var sb = new StringBuilder();

        while (true)
        {
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) ||
                sb.ToString().EndsWith("\r\n", StringComparison.Ordinal))
            {
                // We may have already consumed headers or we're at the end
                break;
            }

            if (stream.DataAvailable)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            }
            else
            {
                break;
            }
        }
    }

    internal static (string? Method, string? Host, int Port) ParseConnectRequest(string requestLine)
    {
        // Format: CONNECT host:port HTTP/1.1
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (null, null, 0);

        var method = parts[0];
        var target = parts[1];

        var colonIndex = target.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(target[(colonIndex + 1)..], out var port))
        {
            var host = target[..colonIndex];
            return (method, host, port);
        }

        return (method, target, 443); // Default HTTPS port
    }

    public bool IsDomainAllowed(string host)
    {
        return _allowedDomains.Contains(host);
    }

    private static async Task SendResponseAsync(NetworkStream stream, string response, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
