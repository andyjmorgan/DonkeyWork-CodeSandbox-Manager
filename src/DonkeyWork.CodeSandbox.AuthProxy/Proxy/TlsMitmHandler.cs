using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace DonkeyWork.CodeSandbox.AuthProxy.Proxy;

public class TlsMitmHandler
{
    private readonly CertificateGenerator _certGenerator;
    private readonly ILogger<TlsMitmHandler> _logger;
    private const int BufferSize = 8192;

    public TlsMitmHandler(CertificateGenerator certGenerator, ILogger<TlsMitmHandler> logger)
    {
        _certGenerator = certGenerator;
        _logger = logger;
    }

    public async Task HandleMitmConnectionAsync(
        Stream clientStream, string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        // Generate a certificate for the target domain signed by our CA
        var domainCert = _certGenerator.GetOrCreateCertificate(targetHost);

        // Wrap the client connection in TLS (server mode — we present our cert to the sandbox)
        var clientSsl = new SslStream(clientStream, leaveInnerStreamOpen: true);
        try
        {
            await clientSsl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = domainCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TLS handshake failed with client for {Host}", targetHost);
            clientSsl.Dispose();
            return;
        }

        // Connect to the real upstream
        TcpClient? upstreamTcp = null;
        SslStream? upstreamSsl = null;
        try
        {
            upstreamTcp = new TcpClient();
            await upstreamTcp.ConnectAsync(targetHost, targetPort, cancellationToken);

            upstreamSsl = new SslStream(upstreamTcp.GetStream(), leaveInnerStreamOpen: false,
                // Accept the upstream's real certificate
                (sender, certificate, chain, errors) => true);

            await upstreamSsl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13
                },
                cancellationToken);

            _logger.LogInformation("Upstream TLS connection established to {Host}:{Port}", targetHost, targetPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to upstream {Host}:{Port}", targetHost, targetPort);
            clientSsl.Dispose();
            upstreamSsl?.Dispose();
            upstreamTcp?.Dispose();
            return;
        }

        // Bidirectional streaming between client and upstream
        try
        {
            var clientToUpstream = CopyStreamAsync(clientSsl, upstreamSsl, "client->upstream", targetHost, cancellationToken);
            var upstreamToClient = CopyStreamAsync(upstreamSsl, clientSsl, "upstream->client", targetHost, cancellationToken);

            await Task.WhenAny(clientToUpstream, upstreamToClient);

            _logger.LogDebug("Connection closed for {Host}", targetHost);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("Connection terminated for {Host}: {Message}", targetHost, ex.Message);
        }
        finally
        {
            clientSsl.Dispose();
            upstreamSsl.Dispose();
            upstreamTcp.Dispose();
        }
    }

    private async Task CopyStreamAsync(
        Stream source, Stream destination, string direction, string host, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug("{Direction} stream ended for {Host}: {Message}", direction, host, ex.Message);
        }
    }
}
