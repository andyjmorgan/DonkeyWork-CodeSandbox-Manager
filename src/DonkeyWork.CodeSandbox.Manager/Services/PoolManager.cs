using System.Net;
using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services;

public class PoolManager : IPoolManager
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<PoolManager> _logger;
    private readonly IContainerRegistry _registry;

    // Pool status labels
    private const string PoolStatusLabel = "pool-status";
    private const string PoolStatusWarm = "warm";
    private const string PoolStatusAllocated = "allocated";
    private const string PoolStatusCreating = "creating";
    private const string AllocatedToLabel = "allocated-to";
    private const string AllocatedAtLabel = "allocated-at";
    private const string ManagerIdLabel = "manager-id";

    public PoolManager(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<PoolManager> logger,
        IContainerRegistry registry)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
        _registry = registry;
    }

    public async Task<KataContainerInfo?> AllocateWarmSandboxAsync(string userId, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 5;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Find warm pods
                var warmPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _config.TargetNamespace,
                    labelSelector: $"{PoolStatusLabel}={PoolStatusWarm}",
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

                // Atomically update labels using resourceVersion (optimistic locking)
                pod.Metadata.Labels[PoolStatusLabel] = PoolStatusAllocated;
                pod.Metadata.Labels[AllocatedToLabel] = userId;
                pod.Metadata.Labels[AllocatedAtLabel] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

                var updatedPod = await _client.CoreV1.ReplaceNamespacedPodAsync(
                    pod,
                    podName,
                    _config.TargetNamespace,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully allocated sandbox {PodName} to user {UserId}",
                    podName, userId);

                // Register/update activity in registry
                _registry.RegisterContainer(podName, DateTime.UtcNow);

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
                labelSelector: $"{PoolStatusLabel}={PoolStatusWarm}",
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
                labelSelector: $"{PoolStatusLabel}={PoolStatusAllocated}",
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
                labelSelector: $"{PoolStatusLabel}={PoolStatusCreating}",
                cancellationToken: cancellationToken);

            return creatingPods.Items.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get creating count");
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

            await Task.WhenAll(creatingTask, warmTask, allocatedTask);

            var creating = await creatingTask;
            var warm = await warmTask;
            var allocated = await allocatedTask;
            var total = creating + warm + allocated;

            var readyPercentage = _config.WarmPoolSize > 0
                ? (warm / (double)_config.WarmPoolSize) * 100.0
                : 0.0;

            var utilizationPercentage = total > 0
                ? (allocated / (double)total) * 100.0
                : 0.0;

            return new PoolStatistics
            {
                Creating = creating,
                Warm = warm,
                Allocated = allocated,
                Total = total,
                TargetSize = _config.WarmPoolSize,
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
                Total = 0,
                TargetSize = _config.WarmPoolSize,
                ReadyPercentage = 0,
                UtilizationPercentage = 0
            };
        }
    }

    public async Task BackfillPoolAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Count all pods in pipeline (creating, warm)
            var creatingPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusCreating}",
                cancellationToken: cancellationToken);

            var warmPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolStatusLabel}={PoolStatusWarm}",
                cancellationToken: cancellationToken);

            var totalInPipeline = creatingPods.Items.Count + warmPods.Items.Count;

            _logger.LogInformation(
                "Pool status: {Creating} creating, {Warm} warm, {Target} target, {Total} in pipeline",
                creatingPods.Items.Count,
                warmPods.Items.Count,
                _config.WarmPoolSize,
                totalInPipeline);

            if (totalInPipeline >= _config.WarmPoolSize)
            {
                _logger.LogDebug("Pool is full, no backfill needed");
                return;
            }

            var deficit = _config.WarmPoolSize - totalInPipeline;
            _logger.LogInformation("Pool deficit: {Deficit}, creating new warm sandboxes", deficit);

            // Create pods to fill the deficit
            var tasks = new List<Task>();
            for (int i = 0; i < deficit; i++)
            {
                tasks.Add(CreateWarmSandboxAsync(cancellationToken));
            }

            await Task.WhenAll(tasks);

            _logger.LogInformation("Backfill completed, created {Count} new warm sandboxes", deficit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pool backfill");
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

    private async Task CreateWarmSandboxAsync(CancellationToken cancellationToken)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var podName = $"{_config.PodNamePrefix}-warm-{uniqueId}";

        try
        {
            var pod = BuildWarmPodSpec(podName);

            _logger.LogInformation("Creating warm sandbox {PodName}", podName);

            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(
                pod,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            _registry.RegisterContainer(podName, DateTime.UtcNow);

            _logger.LogInformation("Warm sandbox {PodName} created, waiting for ready state", podName);

            // Start background task to monitor this pod and update its status to "warm" when ready
            _ = Task.Run(async () => await MonitorPodReadinessAsync(podName, cancellationToken), cancellationToken);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Pod {PodName} already exists, skipping creation", podName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create warm sandbox {PodName}", podName);
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

    private V1Pod BuildWarmPodSpec(string podName)
    {
        var managerId = Environment.MachineName;

        var labels = new Dictionary<string, string>
        {
            ["app"] = "kata-manager",
            ["runtime"] = "kata",
            ["managed-by"] = "CodeSandbox-Manager",
            ["created-at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            [PoolStatusLabel] = PoolStatusCreating,  // Start as "creating"
            [ManagerIdLabel] = managerId
        };

        var container = new V1Container
        {
            Name = "workload",
            Image = _config.DefaultImage,
            ImagePullPolicy = "Always",
            Resources = BuildResourceRequirements(),
            Stdin = true,
            Tty = true
        };

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = _config.TargetNamespace,
                Labels = labels
            },
            Spec = new V1PodSpec
            {
                RuntimeClassName = _config.RuntimeClassName,
                RestartPolicy = "Never",
                Containers = new List<V1Container> { container }
            }
        };

        return pod;
    }

    private V1ResourceRequirements BuildResourceRequirements()
    {
        var requests = new Dictionary<string, ResourceQuantity>
        {
            ["memory"] = new ResourceQuantity($"{_config.DefaultResourceRequests.MemoryMi}Mi"),
            ["cpu"] = new ResourceQuantity($"{_config.DefaultResourceRequests.CpuMillicores}m")
        };

        var limits = new Dictionary<string, ResourceQuantity>
        {
            ["memory"] = new ResourceQuantity($"{_config.DefaultResourceLimits.MemoryMi}Mi"),
            ["cpu"] = new ResourceQuantity($"{_config.DefaultResourceLimits.CpuMillicores}m")
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

            _registry.UnregisterContainer(podName);

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
            LastActivity = _registry.GetLastActivity(pod.Metadata.Name)
        };
    }
}
