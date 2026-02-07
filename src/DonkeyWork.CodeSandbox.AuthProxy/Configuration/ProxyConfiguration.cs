using System.ComponentModel.DataAnnotations;

namespace DonkeyWork.CodeSandbox.AuthProxy.Configuration;

public class ProxyConfiguration
{
    [Range(1, 65535)]
    public int ProxyPort { get; set; } = 8080;

    [Range(1, 65535)]
    public int HealthPort { get; set; } = 8081;

    public List<string> AllowedDomains { get; set; } = new();

    public string CaCertificatePath { get; set; } = "/certs/ca.crt";

    public string CaPrivateKeyPath { get; set; } = "/certs/ca.key";
}
