using DonkeyWork.CodeSandbox.AuthProxy.Configuration;
using DonkeyWork.CodeSandbox.AuthProxy.Proxy;

namespace DonkeyWork.CodeSandbox.AuthProxy.Tests;

public class ProxyServerTests
{
    [Theory]
    [InlineData("CONNECT graph.microsoft.com:443 HTTP/1.1", "CONNECT", "graph.microsoft.com", 443)]
    [InlineData("CONNECT api.github.com:443 HTTP/1.1", "CONNECT", "api.github.com", 443)]
    [InlineData("CONNECT example.com:8443 HTTP/1.1", "CONNECT", "example.com", 8443)]
    [InlineData("GET / HTTP/1.1", "GET", "/", 443)]
    public void ParseConnectRequest_ParsesCorrectly(
        string requestLine, string expectedMethod, string expectedHost, int expectedPort)
    {
        var (method, host, port) = ProxyServer.ParseConnectRequest(requestLine);

        Assert.Equal(expectedMethod, method);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CONNECT")]
    public void ParseConnectRequest_InvalidInput_ReturnsNull(string requestLine)
    {
        var (method, host, port) = ProxyServer.ParseConnectRequest(requestLine);

        if (string.IsNullOrEmpty(requestLine))
        {
            Assert.Null(method);
        }
    }

    [Fact]
    public void IsDomainAllowed_AllowedDomain_ReturnsTrue()
    {
        var config = new ProxyConfiguration
        {
            AllowedDomains = new List<string> { "graph.microsoft.com", "api.github.com" }
        };
        var server = CreateProxyServer(config);

        Assert.True(server.IsDomainAllowed("graph.microsoft.com"));
        Assert.True(server.IsDomainAllowed("api.github.com"));
    }

    [Fact]
    public void IsDomainAllowed_BlockedDomain_ReturnsFalse()
    {
        var config = new ProxyConfiguration
        {
            AllowedDomains = new List<string> { "graph.microsoft.com" }
        };
        var server = CreateProxyServer(config);

        Assert.False(server.IsDomainAllowed("example.com"));
        Assert.False(server.IsDomainAllowed("evil.com"));
    }

    [Fact]
    public void IsDomainAllowed_CaseInsensitive()
    {
        var config = new ProxyConfiguration
        {
            AllowedDomains = new List<string> { "Graph.Microsoft.COM" }
        };
        var server = CreateProxyServer(config);

        Assert.True(server.IsDomainAllowed("graph.microsoft.com"));
        Assert.True(server.IsDomainAllowed("GRAPH.MICROSOFT.COM"));
    }

    [Fact]
    public void IsDomainAllowed_EmptyAllowlist_BlocksEverything()
    {
        var config = new ProxyConfiguration
        {
            AllowedDomains = new List<string>()
        };
        var server = CreateProxyServer(config);

        Assert.False(server.IsDomainAllowed("graph.microsoft.com"));
        Assert.False(server.IsDomainAllowed("example.com"));
    }

    private static ProxyServer CreateProxyServer(ProxyConfiguration config)
    {
        var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        var certGenLogger = loggerFactory.CreateLogger<CertificateGenerator>();
        var mitmLogger = loggerFactory.CreateLogger<TlsMitmHandler>();
        var proxyLogger = loggerFactory.CreateLogger<ProxyServer>();

        var caCert = CertificateGenerator.GenerateEphemeralCa();
        var certGen = new CertificateGenerator(caCert, certGenLogger);
        var mitmHandler = new TlsMitmHandler(certGen, mitmLogger);

        return new ProxyServer(config, mitmHandler, proxyLogger);
    }
}
