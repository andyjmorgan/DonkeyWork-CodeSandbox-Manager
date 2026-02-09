using DonkeyWork.CodeSandbox.AuthProxy.Proxy;

namespace DonkeyWork.CodeSandbox.AuthProxy.Tests;

public class CertificateGeneratorTests
{
    [Fact]
    public void GenerateEphemeralCa_CreatesCaCertificate()
    {
        using var caCert = CertificateGenerator.GenerateEphemeralCa();

        Assert.NotNull(caCert);
        Assert.True(caCert.HasPrivateKey);
        Assert.Contains("DonkeyWork CodeSandbox Internal CA", caCert.Subject);
    }

    [Fact]
    public void GetOrCreateCertificate_GeneratesDomainCert()
    {
        using var caCert = CertificateGenerator.GenerateEphemeralCa();
        var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        var logger = loggerFactory.CreateLogger<CertificateGenerator>();

        using var certGen = new CertificateGenerator(caCert, logger);
        var domainCert = certGen.GetOrCreateCertificate("graph.microsoft.com");

        Assert.NotNull(domainCert);
        Assert.True(domainCert.HasPrivateKey);
        Assert.Contains("graph.microsoft.com", domainCert.Subject);
    }

    [Fact]
    public void GetOrCreateCertificate_CachesCertificates()
    {
        using var caCert = CertificateGenerator.GenerateEphemeralCa();
        var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        var logger = loggerFactory.CreateLogger<CertificateGenerator>();

        using var certGen = new CertificateGenerator(caCert, logger);
        var cert1 = certGen.GetOrCreateCertificate("example.com");
        var cert2 = certGen.GetOrCreateCertificate("example.com");

        Assert.Same(cert1, cert2);
    }

    [Fact]
    public void GetOrCreateCertificate_DifferentDomainsGetDifferentCerts()
    {
        using var caCert = CertificateGenerator.GenerateEphemeralCa();
        var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        var logger = loggerFactory.CreateLogger<CertificateGenerator>();

        using var certGen = new CertificateGenerator(caCert, logger);
        var cert1 = certGen.GetOrCreateCertificate("example.com");
        var cert2 = certGen.GetOrCreateCertificate("other.com");

        Assert.NotSame(cert1, cert2);
        Assert.Contains("example.com", cert1.Subject);
        Assert.Contains("other.com", cert2.Subject);
    }
}
