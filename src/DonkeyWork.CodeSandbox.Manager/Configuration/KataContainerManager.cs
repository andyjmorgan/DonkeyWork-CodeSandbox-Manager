using System.ComponentModel.DataAnnotations;

namespace DonkeyWork.CodeSandbox.Manager.Configuration;

public class KataContainerManager
{
    [Required]
    [RegularExpression(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", ErrorMessage = "Invalid namespace name")]
    public string TargetNamespace { get; set; } = "sandbox-containers";

    [Required]
    [RegularExpression(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", ErrorMessage = "Invalid runtime class name")]
    public string RuntimeClassName { get; set; } = "kata-qemu";

    [Required]
    public ResourceConfig DefaultResourceRequests { get; set; } = new();

    [Required]
    public ResourceConfig DefaultResourceLimits { get; set; } = new();

    [Required]
    [RegularExpression(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", ErrorMessage = "Invalid pod name prefix")]
    public string PodNamePrefix { get; set; } = "kata-sandbox";

    [Required]
    public string DefaultImage { get; set; } = "ghcr.io/andyjmorgan/donkeywork-codesandbox-api:latest";

    public bool CleanupCompletedPods { get; set; } = true;

    [Range(30, 300, ErrorMessage = "Pod ready timeout must be between 30 and 300 seconds")]
    public int PodReadyTimeoutSeconds { get; set; } = 90;

    [Range(1, 1440, ErrorMessage = "Idle timeout must be between 1 and 1440 minutes")]
    public int IdleTimeoutMinutes { get; set; } = 5;

    [Range(1, 60, ErrorMessage = "Cleanup check interval must be between 1 and 60 minutes")]
    public int CleanupCheckIntervalMinutes { get; set; } = 1;

    [Range(1, 1440, ErrorMessage = "Maximum container lifetime must be between 1 and 1440 minutes")]
    public int MaxContainerLifetimeMinutes { get; set; } = 15;

    // Pool management settings
    [Range(1, 500, ErrorMessage = "Max total containers must be between 1 and 500")]
    public int MaxTotalContainers { get; set; } = 50;

    [Range(0, 100, ErrorMessage = "Warm pool size must be between 0 and 100")]
    public int WarmPoolSize { get; set; } = 10;

    [Range(10, 300, ErrorMessage = "Pool backfill check interval must be between 10 and 300 seconds")]
    public int PoolBackfillCheckIntervalSeconds { get; set; } = 30;

    [Range(5, 60, ErrorMessage = "Lease duration must be between 5 and 60 seconds")]
    public int LeaderLeaseDurationSeconds { get; set; } = 15;

    // MCP Server settings
    [Required]
    public string McpServerImage { get; set; } = "ghcr.io/andyjmorgan/donkeywork-codesandbox-mcpserver:latest";

    [Required]
    [RegularExpression(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", ErrorMessage = "Invalid MCP pod name prefix")]
    public string McpPodNamePrefix { get; set; } = "kata-mcp";

    [Range(0, 100, ErrorMessage = "MCP warm pool size must be between 0 and 100")]
    public int McpWarmPoolSize { get; set; } = 5;

    [Range(1, 1440, ErrorMessage = "MCP idle timeout must be between 1 and 1440 minutes")]
    public int McpIdleTimeoutMinutes { get; set; } = 60;

    [Range(1, 1440, ErrorMessage = "MCP max container lifetime must be between 1 and 1440 minutes")]
    public int McpMaxContainerLifetimeMinutes { get; set; } = 480;

    // Optional separate resource config for MCP servers (falls back to Default if null)
    public ResourceConfig? McpResourceRequests { get; set; }
    public ResourceConfig? McpResourceLimits { get; set; }

    // Auth proxy sidecar settings
    public bool EnableAuthProxy { get; set; } = false;

    public string AuthProxyImage { get; set; } = "ghcr.io/andyjmorgan/donkeywork-codesandbox-authproxy:latest";

    public ResourceConfig AuthProxySidecarResourceRequests { get; set; } = new() { MemoryMi = 64, CpuMillicores = 100 };
    public ResourceConfig AuthProxySidecarResourceLimits { get; set; } = new() { MemoryMi = 128, CpuMillicores = 250 };

    [Range(1, 65535, ErrorMessage = "Auth proxy port must be between 1 and 65535")]
    public int AuthProxyPort { get; set; } = 8080;

    [Range(1, 65535, ErrorMessage = "Auth proxy health port must be between 1 and 65535")]
    public int AuthProxyHealthPort { get; set; } = 8081;

    public List<string> AuthProxyAllowedDomains { get; set; } = new()
    {
        "graph.microsoft.com",
        "api.github.com",
        "github.com"
    };

    public string AuthProxyCaSecretName { get; set; } = "sandbox-proxy-ca";

    // Optional: Direct k8s connection (alternative to kubeconfig)
    public KubernetesConnectionConfig? Connection { get; set; }
}

public class KubernetesConnectionConfig
{
    public string? ServerUrl { get; set; }
    public string? Token { get; set; }
    public bool SkipTlsVerify { get; set; } = false;
}

public class ResourceConfig
{
    [Range(1, 65536, ErrorMessage = "Memory must be between 1Mi and 65536Mi")]
    public int MemoryMi { get; set; }

    [Range(1, 64000, ErrorMessage = "CPU must be between 1m and 64000m")]
    public int CpuMillicores { get; set; }
}
