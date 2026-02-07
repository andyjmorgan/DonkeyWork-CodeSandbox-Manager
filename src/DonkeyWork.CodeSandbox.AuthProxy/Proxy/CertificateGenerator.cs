using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DonkeyWork.CodeSandbox.AuthProxy.Proxy;

public class CertificateGenerator : IDisposable
{
    private readonly X509Certificate2 _caCertificate;
    private readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new();
    private readonly ILogger<CertificateGenerator> _logger;

    public CertificateGenerator(X509Certificate2 caCertificate, ILogger<CertificateGenerator> logger)
    {
        _caCertificate = caCertificate;
        _logger = logger;
    }

    public X509Certificate2 GetOrCreateCertificate(string hostname)
    {
        return _certCache.GetOrAdd(hostname, CreateCertificateForHost);
    }

    private X509Certificate2 CreateCertificateForHost(string hostname)
    {
        _logger.LogDebug("Generating certificate for {Hostname}", hostname);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostname}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, // serverAuth
                false));

        // Add Subject Alternative Name
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostname);
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Sign with the CA certificate
        using var caPrivateKey = _caCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("CA certificate does not have a private key");

        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F; // Ensure positive

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        var cert = request.Create(
            _caCertificate.IssuerName,
            X509SignatureGenerator.CreateForRSA(caPrivateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            serialNumber);

        // Combine the cert with its private key
        var certWithKey = cert.CopyWithPrivateKey(rsa);

        // Export and re-import to make the cert usable on all platforms
        var pfxBytes = certWithKey.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 LoadOrGenerateCaCertificate(
        string certPath, string keyPath, ILogger logger)
    {
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            logger.LogInformation("Loading CA certificate from {CertPath}", certPath);
            return LoadCaCertificateFromPem(certPath, keyPath);
        }

        logger.LogWarning("CA certificate not found at {CertPath}, generating ephemeral CA for development", certPath);
        return GenerateEphemeralCa();
    }

    private static X509Certificate2 LoadCaCertificateFromPem(string certPath, string keyPath)
    {
        var certPem = File.ReadAllText(certPath);
        var keyPem = File.ReadAllText(keyPath);

        var cert = X509Certificate2.CreateFromPem(certPem, keyPem);

        // Export and re-import for full key access
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 GenerateEphemeralCa()
    {
        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest(
            "CN=DonkeyWork CodeSandbox Internal CA, O=DonkeyWork, OU=CodeSandbox",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: true,
                pathLengthConstraint: 0,
                critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(365));

        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        _caCertificate.Dispose();
        foreach (var cert in _certCache.Values)
        {
            cert.Dispose();
        }
        _certCache.Clear();
    }
}
