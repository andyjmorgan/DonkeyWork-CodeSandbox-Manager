using System.Net;
using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services.Pool;

public class PoolManager : IPoolManager
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<PoolManager> _logger;

    // Pool status labels (for k8s label selectors)
    private const string PoolStatusLabel = "pool-status";
    private const string PoolStatusWarm = "warm";
    private const string PoolStatusAllocated = "allocated";
    private const string PoolStatusCreating = "creating";
    internal const string PoolStatusManual = "manual";
    private const string AllocatedToLabel = "allocated-to";
    private const string ManagerIdLabel = "manager-id";

    // Container type labels
    internal const string ContainerTypeLabel = "container-type";
    internal const string ContainerTypeSandbox = "sandbox";
    internal const string ContainerTypeMcp = "mcp-server";

    // Annotation for storing MCP launch command
    internal const string McpLaunchCommandAnnotation = "codesandbox.donkeywork.dev/mcp-launch-command";

    // Annotations for timestamps (source of truth for time-based data)
    internal const string CreatedAtAnnotation = "codesandbox.donkeywork.dev/created-at";
    internal const string AllocatedAtAnnotation = "codesandbox.donkeywork.dev/allocated-at";
    internal const string LastActivityAnnotation = "codesandbox.donkeywork.dev/last-activity";

    public PoolManager(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<PoolManager> logger)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<KataContainerInfo?> AllocateWarmSandboxAsync(string userId, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 5;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Find warm sandbox pods (not MCP)
                var warmPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _config.TargetNamespace,
                    labelSelector: $"{PoolStatusLabel}={PoolStatusWarm},{ContainerTypeLabel}={ContainerTypeSandbox}",
                    cancellationToken: cancellationToken);

                if (!warmPods.Items.Any())
                {
                    _logger.LogWarning("No warm sandboxes available in pool (attempt {Attempt}/{MaxRetries})",
                        attempt + 1, maxRetries);

                    if (attempt < maxRetries - 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                        continue;
                    }

                    return null;
                }

                // Pick the first warm pod
                var pod = warmPods.Items.First();
                var podName = pod.Metadata.Name;

                _logger.LogInformation("Attempting to allocate warm sandbox {PodName} to user {UserId}",
                    podName, userId);

                // Atomically update labels and annotations using resourceVersion (optimistic locking)
                var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

                pod.Metadata.Labels[PoolStatusLabel] = PoolStatusAllocated;
                pod.Metadata.Labels[AllocatedToLabel] = userId;

                // Use annotations for timestamps (source of truth)
                pod.Metadata.Annotations ??= new Dictionary<string, string>();
                pod.Metadata.Annotations[AllocatedAtAnnotation] = nowTimestamp;
                pod.Metadata.Annotations[LastActivityAnnotation] = nowTimestamp;

                var updatedPod = await _client.CoreV1.ReplaceNamespacedPodAsync(
                    pod,
                    podName,
                    _config.TargetNamespace,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully allocated sandbox {PodName} to user {UserId}",
                    podName, userId);

                return MapPodToContainerInfo(updatedPod);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                // Race condition - someone else allocated this pod first, retry with different pod
                _logger.LogDebug("Allocation conflict on attempt {Attempt}, retrying with different pod",
                    attempt + 1);

                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error allocating warm sandbox on attempt {Attempt}", attempt + 1);
                throw;
            }
        }

        _logger.LogError("Failed to allocate warm sandbox after {MaxRetries} attempts", maxRetries);
        return null;
    }

    public async Task<int> GetWarmPoolCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var warmPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusWarm},{ContainerTypeLabel}={ContainerTypeSandbox}",
                cancellationToken: cancellationToken);

            return warmPods.Items.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get warm pool count");
            return 0;
        }
    }

    public async Task<int> GetAllocatedCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allocatedPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusAllocated},{ContainerTypeLabel}={ContainerTypeSandbox}",
                cancellationToken: cancellationToken);

            return allocatedPods.Items.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get allocated count");
            return 0;
        }
    }

    public async Task<int> GetCreatingCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var creatingPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusCreating},{ContainerTypeLabel}={ContainerTypeSandbox}",
                cancellationToken: cancellationToken);

            return creatingPods.Items.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get creating count");
            return 0;
        }
    }

    public async Task<int> GetManualCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manualPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusManual},{ContainerTypeLabel}={ContainerTypeSandbox}",
                cancellationToken: cancellationToken);

            return manualPods.Items.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get manual count");
            return 0;
        }
    }

    public async Task<int> GetTotalContainerCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            return allPods.Items
                .Count(p => p.Spec.RuntimeClassName == _config.RuntimeClassName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get total container count");
            return 0;
        }
    }

    public async Task<PoolStatistics> GetPoolStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Fetch all counts in parallel for efficiency
            var creatingTask = GetCreatingCountAsync(cancellationToken);
            var warmTask = GetWarmPoolCountAsync(cancellationToken);
            var allocatedTask = GetAllocatedCountAsync(cancellationToken);
            var manualTask = GetManualCountAsync(cancellationToken);

            await Task.WhenAll(creatingTask, warmTask, allocatedTask, manualTask);

            var creating = await creatingTask;
            var warm = await warmTask;
            var allocated = await allocatedTask;
            var manual = await manualTask;
            var total = creating + warm + allocated + manual;

            var readyPercentage = _config.WarmPoolSize > 0
                ? (warm / (double)_config.WarmPoolSize) * 100.0
                : 0.0;

            // Utilization = (allocated + manual) / total
            var activeContainers = allocated + manual;
            var utilizationPercentage = total > 0
                ? (activeContainers / (double)total) * 100.0
                : 0.0;

            return new PoolStatistics
            {
                Creating = creating,
                Warm = warm,
                Allocated = allocated,
                Manual = manual,
                Total = total,
                TargetSize = _config.WarmPoolSize,
                MaxTotalContainers = _config.MaxTotalContainers,
                ReadyPercentage = Math.Round(readyPercentage, 1),
                UtilizationPercentage = Math.Round(utilizationPercentage, 1)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pool statistics");
            return new PoolStatistics
            {
                Creating = 0,
                Warm = 0,
                Allocated = 0,
                Manual = 0,
                Total = 0,
                TargetSize = _config.WarmPoolSize,
                MaxTotalContainers = _config.MaxTotalContainers,
                ReadyPercentage = 0,
                UtilizationPercentage = 0
            };
        }
    }

    public async Task BackfillPoolAsync(CancellationToken cancellationToken = default)
    {
        // Get total container count to enforce max limit (shared across both pool types)
        var totalContainers = await GetTotalContainerCountAsync(cancellationToken);

        // Backfill sandbox pool
        await BackfillPoolForTypeAsync(
            ContainerTypeSandbox,
            _config.WarmPoolSize,
            totalContainers,
            cancellationToken);

        // Re-count after sandbox backfill
        totalContainers = await GetTotalContainerCountAsync(cancellationToken);

        // Backfill MCP pool
        if (_config.McpWarmPoolSize > 0)
        {
            await BackfillPoolForTypeAsync(
                ContainerTypeMcp,
                _config.McpWarmPoolSize,
                totalContainers,
                cancellationToken);
        }
    }

    private async Task BackfillPoolForTypeAsync(
        string containerType, int targetSize, int totalContainers,
        CancellationToken cancellationToken)
    {
        var typeLabel = containerType == ContainerTypeMcp ? "MCP" : "sandbox";

        try
        {
            var creatingPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusCreating},{ContainerTypeLabel}={containerType}",
                cancellationToken: cancellationToken);

            var warmPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusWarm},{ContainerTypeLabel}={containerType}",
                cancellationToken: cancellationToken);

            var totalInPipeline = creatingPods.Items.Count + warmPods.Items.Count;

            _logger.LogInformation(
                "{Type} pool status: {Creating} creating, {Warm} warm, {Target} target, {Total} in pipeline, {TotalContainers}/{MaxContainers} total",
                typeLabel,
                creatingPods.Items.Count,
                warmPods.Items.Count,
                targetSize,
                totalInPipeline,
                totalContainers,
                _config.MaxTotalContainers);

            if (totalInPipeline >= targetSize)
            {
                _logger.LogDebug("{Type} pool is full, no backfill needed", typeLabel);
                return;
            }

            var deficit = targetSize - totalInPipeline;
            var availableCapacity = _config.MaxTotalContainers - totalContainers;
            if (availableCapacity <= 0)
            {
                _logger.LogWarning("Max container limit reached ({Max}), cannot backfill {Type} pool",
                    _config.MaxTotalContainers, typeLabel);
                return;
            }

            var toCreate = Math.Min(deficit, availableCapacity);
            if (toCreate < deficit)
            {
                _logger.LogWarning("Limiting {Type} backfill to {ToCreate} (requested {Deficit}) due to max container limit",
                    typeLabel, toCreate, deficit);
            }

            _logger.LogInformation("{Type} pool deficit: {Deficit}, creating {ToCreate} new warm containers",
                typeLabel, deficit, toCreate);

            var tasks = new List<Task>();
            for (int i = 0; i < toCreate; i++)
            {
                tasks.Add(CreateWarmContainerAsync(containerType, cancellationToken));
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("{Type} backfill completed, created {Count} containers", typeLabel, toCreate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during {Type} pool backfill", typeLabel);
        }
    }

    public async Task MonitorAndCleanupFailedPodsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Find all pods with our runtime class
            var allPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            var kataPods = allPods.Items
                .Where(p => p.Spec.RuntimeClassName == _config.RuntimeClassName)
                .ToList();

            foreach (var pod in kataPods)
            {
                var podName = pod.Metadata.Name;
                var phase = pod.Status?.Phase;

                // Handle Failed pods
                if (phase == "Failed")
                {
                    _logger.LogWarning(
                        "Pod {PodName} in Failed state. Reason: {Reason}, Message: {Message}",
                        podName,
                        pod.Status?.Reason ?? "Unknown",
                        pod.Status?.Message ?? "No message");

                    string? poolStatus = null;
                    pod.Metadata.Labels?.TryGetValue(PoolStatusLabel, out poolStatus);

                    // Delete the failed pod
                    await DeletePodAsync(podName, cancellationToken);

                    // If it was part of the pool (creating or warm), trigger backfill
                    if (poolStatus is PoolStatusCreating or PoolStatusWarm)
                    {
                        _logger.LogInformation("Failed pod {PodName} was part of warm pool, will be replaced in next backfill",
                            podName);
                    }
                }
                // Handle Succeeded pods (completed successfully but should be cleaned up)
                else if (phase == "Succeeded")
                {
                    _logger.LogInformation("Pod {PodName} in Succeeded state, cleaning up", podName);

                    string? poolStatus = null;
                    pod.Metadata.Labels?.TryGetValue(PoolStatusLabel, out poolStatus);

                    await DeletePodAsync(podName, cancellationToken);

                    // If it was part of the pool, trigger backfill
                    if (poolStatus is PoolStatusCreating or PoolStatusWarm)
                    {
                        _logger.LogInformation("Succeeded pod {PodName} was part of warm pool, will be replaced in next backfill",
                            podName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring and cleaning up failed pods");
        }
    }

    private async Task CreateWarmContainerAsync(string containerType, CancellationToken cancellationToken)
    {
        var isMcp = containerType == ContainerTypeMcp;
        var prefix = isMcp ? _config.McpPodNamePrefix : _config.PodNamePrefix;
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var podName = $"{prefix}-warm-{uniqueId}";
        var typeLabel = isMcp ? "MCP" : "sandbox";

        try
        {
            var pod = BuildWarmPodSpec(podName, containerType);

            _logger.LogInformation("Creating warm {Type} {PodName}", typeLabel, podName);

            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(
                pod,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Warm {Type} {PodName} created, waiting for ready state", typeLabel, podName);

            _ = Task.Run(async () => await MonitorPodReadinessAsync(podName, cancellationToken), cancellationToken);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Pod {PodName} already exists, skipping creation", podName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create warm {Type} {PodName}", typeLabel, podName);
        }
    }

    private async Task MonitorPodReadinessAsync(string podName, CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(_config.PodReadyTimeoutSeconds);
            var deadline = DateTime.UtcNow.Add(timeout);
            var pollInterval = TimeSpan.FromSeconds(2);
            var lastLoggedStatus = "";

            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                    podName,
                    _config.TargetNamespace,
                    cancellationToken: cancellationToken);

                // Log detailed container status for visibility
                var containerStatus = pod.Status?.ContainerStatuses?.FirstOrDefault();
                if (containerStatus != null)
                {
                    var currentStatus = GetContainerStatusDescription(containerStatus);
                    if (currentStatus != lastLoggedStatus)
                    {
                        _logger.LogInformation("Pod {PodName} status: {Status}", podName, currentStatus);
                        lastLoggedStatus = currentStatus;
                    }
                }

                if (IsPodReady(pod))
                {
                    _logger.LogInformation("Pod {PodName} is ready, marking as warm", podName);

                    // Update status from "creating" to "warm"
                    pod.Metadata.Labels[PoolStatusLabel] = PoolStatusWarm;

                    await _client.CoreV1.ReplaceNamespacedPodAsync(
                        pod,
                        podName,
                        _config.TargetNamespace,
                        cancellationToken: cancellationToken);

                    _logger.LogInformation("Pod {PodName} successfully marked as warm and ready for allocation", podName);
                    return;
                }

                if (pod.Status?.Phase == "Failed")
                {
                    _logger.LogWarning("Pod {PodName} failed during warmup, will be cleaned up by monitor", podName);
                    return;
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            _logger.LogWarning("Timeout waiting for pod {PodName} to be ready", podName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring pod readiness for {PodName}", podName);
        }
    }

    private string GetContainerStatusDescription(V1ContainerStatus status)
    {
        if (status.State?.Waiting != null)
        {
            var waiting = status.State.Waiting;
            return $"Waiting - {waiting.Reason ?? "Unknown"}: {waiting.Message ?? "No details"}";
        }

        if (status.State?.Running != null)
        {
            return "Running";
        }

        if (status.State?.Terminated != null)
        {
            var terminated = status.State.Terminated;
            return $"Terminated - {terminated.Reason ?? "Unknown"}: {terminated.Message ?? "No details"}";
        }

        return "Unknown";
    }

    private V1Pod BuildWarmPodSpec(string podName, string containerType = ContainerTypeSandbox)
    {
        var managerId = Environment.MachineName;
        var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var isMcp = containerType == ContainerTypeMcp;
        var image = isMcp ? _config.McpServerImage : _config.DefaultImage;

        var labels = new Dictionary<string, string>
        {
            ["app"] = "kata-manager",
            ["runtime"] = "kata",
            ["managed-by"] = "CodeSandbox-Manager",
            [ContainerTypeLabel] = containerType,
            [PoolStatusLabel] = PoolStatusCreating,  // Start as "creating"
            [ManagerIdLabel] = managerId
        };

        // Annotations for timestamps (source of truth)
        var annotations = new Dictionary<string, string>
        {
            [CreatedAtAnnotation] = nowTimestamp,
            [LastActivityAnnotation] = nowTimestamp
        };

        var resourceRequests = isMcp && _config.McpResourceRequests != null ? _config.McpResourceRequests : _config.DefaultResourceRequests;
        var resourceLimits = isMcp && _config.McpResourceLimits != null ? _config.McpResourceLimits : _config.DefaultResourceLimits;

        var container = new V1Container
        {
            Name = "workload",
            Image = image,
            ImagePullPolicy = "Always",
            Resources = BuildResourceRequirements(resourceRequests, resourceLimits),
            Stdin = true,
            Tty = true
        };

        var containers = new List<V1Container> { container };
        var volumes = new List<V1Volume>();

        // Add auth proxy sidecar for sandbox containers when enabled
        if (_config.EnableAuthProxy && !isMcp)
        {
            // Add proxy env vars to workload container
            container.Env = new List<V1EnvVar>
            {
                new() { Name = "HTTP_PROXY", Value = $"http://127.0.0.1:{_config.AuthProxyPort}" },
                new() { Name = "HTTPS_PROXY", Value = $"http://127.0.0.1:{_config.AuthProxyPort}" },
                new() { Name = "NO_PROXY", Value = "localhost,127.0.0.1" },
                new() { Name = "NODE_EXTRA_CA_CERTS", Value = "/etc/proxy-ca/ca.crt" },
            };

            // Mount CA public cert into workload container
            container.VolumeMounts = new List<V1VolumeMount>
            {
                new()
                {
                    Name = "proxy-ca-public",
                    MountPath = "/etc/proxy-ca",
                    ReadOnlyProperty = true
                }
            };

            // Add sidecar container
            containers.Add(BuildAuthProxySidecar());

            // Add volumes for CA cert
            volumes.Add(new V1Volume
            {
                Name = "proxy-ca-public",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = _config.AuthProxyCaSecretName,
                    Items = new List<V1KeyToPath>
                    {
                        new() { Key = "tls.crt", Path = "ca.crt" }
                    }
                }
            });

            volumes.Add(new V1Volume
            {
                Name = "proxy-ca-full",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = _config.AuthProxyCaSecretName,
                    Items = new List<V1KeyToPath>
                    {
                        new() { Key = "tls.crt", Path = "ca.crt" },
                        new() { Key = "tls.key", Path = "ca.key" }
                    }
                }
            });
        }

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = _config.TargetNamespace,
                Labels = labels,
                Annotations = annotations
            },
            Spec = new V1PodSpec
            {
                RuntimeClassName = _config.RuntimeClassName,
                RestartPolicy = "Never",
                Containers = containers,
                Volumes = volumes.Count > 0 ? volumes : null
            }
        };

        return pod;
    }

    private V1Container BuildAuthProxySidecar()
    {
        var envVars = new List<V1EnvVar>
        {
            new() { Name = "ProxyConfiguration__ProxyPort", Value = _config.AuthProxyPort.ToString() },
            new() { Name = "ProxyConfiguration__HealthPort", Value = _config.AuthProxyHealthPort.ToString() },
            new() { Name = "ProxyConfiguration__CaCertificatePath", Value = "/certs/ca.crt" },
            new() { Name = "ProxyConfiguration__CaPrivateKeyPath", Value = "/certs/ca.key" },
        };

        for (int i = 0; i < _config.AuthProxyAllowedDomains.Count; i++)
        {
            envVars.Add(new V1EnvVar
            {
                Name = $"ProxyConfiguration__AllowedDomains__{i}",
                Value = _config.AuthProxyAllowedDomains[i]
            });
        }

        return new V1Container
        {
            Name = "auth-proxy",
            Image = _config.AuthProxyImage,
            ImagePullPolicy = "Always",
            Ports = new List<V1ContainerPort>
            {
                new() { ContainerPort = _config.AuthProxyPort },
                new() { ContainerPort = _config.AuthProxyHealthPort }
            },
            Env = envVars,
            VolumeMounts = new List<V1VolumeMount>
            {
                new()
                {
                    Name = "proxy-ca-full",
                    MountPath = "/certs",
                    ReadOnlyProperty = true
                }
            },
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["memory"] = new ResourceQuantity($"{_config.AuthProxySidecarResourceRequests.MemoryMi}Mi"),
                    ["cpu"] = new ResourceQuantity($"{_config.AuthProxySidecarResourceRequests.CpuMillicores}m")
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["memory"] = new ResourceQuantity($"{_config.AuthProxySidecarResourceLimits.MemoryMi}Mi"),
                    ["cpu"] = new ResourceQuantity($"{_config.AuthProxySidecarResourceLimits.CpuMillicores}m")
                }
            },
            ReadinessProbe = new V1Probe
            {
                HttpGet = new V1HTTPGetAction
                {
                    Path = "/healthz",
                    Port = _config.AuthProxyHealthPort
                },
                InitialDelaySeconds = 2,
                PeriodSeconds = 5
            }
        };
    }

    private V1ResourceRequirements BuildResourceRequirements(
        ResourceConfig? requestConfig = null,
        ResourceConfig? limitConfig = null)
    {
        var reqConfig = requestConfig ?? _config.DefaultResourceRequests;
        var limConfig = limitConfig ?? _config.DefaultResourceLimits;

        var requests = new Dictionary<string, ResourceQuantity>
        {
            ["memory"] = new ResourceQuantity($"{reqConfig.MemoryMi}Mi"),
            ["cpu"] = new ResourceQuantity($"{reqConfig.CpuMillicores}m")
        };

        var limits = new Dictionary<string, ResourceQuantity>
        {
            ["memory"] = new ResourceQuantity($"{limConfig.MemoryMi}Mi"),
            ["cpu"] = new ResourceQuantity($"{limConfig.CpuMillicores}m")
        };

        return new V1ResourceRequirements
        {
            Requests = requests,
            Limits = limits
        };
    }

    private async Task DeletePodAsync(string podName, CancellationToken cancellationToken)
    {
        try
        {
            await _client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                body: new V1DeleteOptions { GracePeriodSeconds = 0 },
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully deleted pod {PodName}", podName);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Pod {PodName} not found, may have been already deleted", podName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete pod {PodName}", podName);
        }
    }

    private bool IsPodReady(V1Pod pod)
    {
        if (pod.Status?.Phase != "Running")
            return false;

        var readyCondition = pod.Status.Conditions?
            .FirstOrDefault(c => c.Type == "Ready");

        return readyCondition?.Status == "True";
    }

    private KataContainerInfo MapPodToContainerInfo(V1Pod pod)
    {
        var readyCondition = pod.Status?.Conditions?
            .FirstOrDefault(c => c.Type == "Ready");

        var containerImage = pod.Spec?.Containers?.FirstOrDefault()?.Image;

        // Get last activity from annotation
        DateTime? lastActivity = null;
        if (pod.Metadata.Annotations?.TryGetValue(LastActivityAnnotation, out var lastActivityStr) == true
            && long.TryParse(lastActivityStr, out var lastActivityUnix))
        {
            lastActivity = DateTimeOffset.FromUnixTimeSeconds(lastActivityUnix).UtcDateTime;
        }

        return new KataContainerInfo
        {
            Name = pod.Metadata.Name,
            Phase = pod.Status?.Phase ?? "Unknown",
            IsReady = readyCondition?.Status == "True",
            CreatedAt = pod.Metadata.CreationTimestamp,
            NodeName = pod.Spec?.NodeName,
            PodIP = pod.Status?.PodIP,
            Labels = pod.Metadata.Labels != null ? new Dictionary<string, string>(pod.Metadata.Labels) : null,
            Image = containerImage,
            LastActivity = lastActivity
        };
    }

    /// <summary>
    /// Helper to parse Unix timestamp from annotation.
    /// </summary>
    internal static DateTime? ParseTimestampAnnotation(IDictionary<string, string>? annotations, string key)
    {
        if (annotations?.TryGetValue(key, out var timestampStr) == true
            && long.TryParse(timestampStr, out var unixTimestamp))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
        }
        return null;
    }
}
