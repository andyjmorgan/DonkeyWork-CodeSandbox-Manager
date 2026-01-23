using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Services.Pool;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services.Background;

/// <summary>
/// Background service that periodically cleans up idle and expired containers
/// using Kubernetes annotations as the source of truth.
/// </summary>
public class ContainerCleanupService : BackgroundService
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<ContainerCleanupService> _logger;

    public ContainerCleanupService(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<ContainerCleanupService> logger)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Container cleanup service started. Idle timeout: {IdleTimeoutMinutes}m, Max lifetime: {MaxLifetimeMinutes}m, Check interval: {CheckIntervalMinutes}m",
            _config.IdleTimeoutMinutes, _config.MaxContainerLifetimeMinutes, _config.CleanupCheckIntervalMinutes);

        var checkInterval = TimeSpan.FromMinutes(_config.CleanupCheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(checkInterval, stoppingToken);

            try
            {
                await CleanupContainersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during container cleanup");
            }
        }

        _logger.LogInformation("Container cleanup service stopped");
    }

    private async Task CleanupContainersAsync(CancellationToken cancellationToken)
    {
        // Clean up allocated containers (from pool)
        var allocatedPods = await _client.CoreV1.ListNamespacedPodAsync(
            _config.TargetNamespace,
            labelSelector: "pool-status=allocated",
            cancellationToken: cancellationToken);

        // Also clean up manually created containers
        var manualPods = await _client.CoreV1.ListNamespacedPodAsync(
            _config.TargetNamespace,
            labelSelector: "pool-status=manual",
            cancellationToken: cancellationToken);

        var allActiveContainers = allocatedPods.Items.Concat(manualPods.Items).ToList();

        if (!allActiveContainers.Any())
        {
            _logger.LogDebug("No active containers to check for cleanup");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var idleThreshold = TimeSpan.FromMinutes(_config.IdleTimeoutMinutes);
        var maxLifetime = TimeSpan.FromMinutes(_config.MaxContainerLifetimeMinutes);

        var idleContainers = new List<V1Pod>();
        var expiredContainers = new List<V1Pod>();

        foreach (var pod in allActiveContainers)
        {
            // Use allocation time for max lifetime checks (not creation time)
            var allocatedAt = PoolManager.ParseTimestampAnnotation(
                pod.Metadata.Annotations,
                PoolManager.AllocatedAtAnnotation);

            var lastActivity = PoolManager.ParseTimestampAnnotation(
                pod.Metadata.Annotations,
                PoolManager.LastActivityAnnotation);

            // Check for expired containers (exceeded max lifetime since allocation)
            if (allocatedAt.HasValue)
            {
                var allocatedAge = now - allocatedAt.Value;
                if (allocatedAge >= maxLifetime)
                {
                    expiredContainers.Add(pod);
                    continue; // Don't double-count
                }
            }

            // Check for idle containers (no activity since last command)
            if (lastActivity.HasValue)
            {
                var idleTime = now - lastActivity.Value;
                if (idleTime >= idleThreshold)
                {
                    idleContainers.Add(pod);
                }
            }
        }

        // Log status
        _logger.LogInformation(
            "Container cleanup check: {Allocated} allocated, {Manual} manual, {Expired} expired, {Idle} idle",
            allocatedPods.Items.Count, manualPods.Items.Count, expiredContainers.Count, idleContainers.Count);

        // Delete expired containers first (hard limit)
        foreach (var pod in expiredContainers)
        {
            await DeletePodAsync(pod.Metadata.Name, "exceeded max lifetime", cancellationToken);
        }

        // Delete idle containers
        foreach (var pod in idleContainers)
        {
            await DeletePodAsync(pod.Metadata.Name, "idle timeout", cancellationToken);
        }
    }

    private async Task DeletePodAsync(string podName, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                body: new V1DeleteOptions { GracePeriodSeconds = 30 },
                cancellationToken: cancellationToken);

            _logger.LogInformation("Deleted container {PodName} ({Reason})", podName, reason);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Pod {PodName} not found, may have been already deleted", podName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete container {PodName}", podName);
        }
    }
}
