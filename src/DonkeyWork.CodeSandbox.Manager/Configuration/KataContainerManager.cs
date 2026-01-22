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
    public int IdleTimeoutMinutes { get; set; } = 15;

    [Range(1, 60, ErrorMessage = "Cleanup check interval must be between 1 and 60 minutes")]
    public int CleanupCheckIntervalMinutes { get; set; } = 1;

    [Range(1, 1440, ErrorMessage = "Maximum container lifetime must be between 1 and 1440 minutes")]
    public int MaxContainerLifetimeMinutes { get; set; } = 60;

    // Pool management settings
    [Range(0, 100, ErrorMessage = "Warm pool size must be between 0 and 100")]
    public int WarmPoolSize { get; set; } = 10;

    [Range(10, 300, ErrorMessage = "Pool backfill check interval must be between 10 and 300 seconds")]
    public int PoolBackfillCheckIntervalSeconds { get; set; } = 30;

    [Range(5, 60, ErrorMessage = "Lease duration must be between 5 and 60 seconds")]
    public int LeaderLeaseDurationSeconds { get; set; } = 15;

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
