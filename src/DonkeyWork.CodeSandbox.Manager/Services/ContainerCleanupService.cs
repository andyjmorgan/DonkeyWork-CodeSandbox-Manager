using DonkeyWork.CodeSandbox.Manager.Configuration;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services;

/// <summary>
/// Background service that initializes the container registry on startup
/// and periodically cleans up idle containers.
/// </summary>
public class ContainerCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IContainerRegistry _registry;
    private readonly KataContainerManager _config;
    private readonly ILogger<ContainerCleanupService> _logger;

    public ContainerCleanupService(
        IServiceProvider serviceProvider,
        IContainerRegistry registry,
        IOptions<KataContainerManager> config,
        ILogger<ContainerCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _registry = registry;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Container cleanup service started. Idle timeout: {IdleTimeoutMinutes}m, Max lifetime: {MaxLifetimeMinutes}m, Check interval: {CheckIntervalMinutes}m",
            _config.IdleTimeoutMinutes, _config.MaxContainerLifetimeMinutes, _config.CleanupCheckIntervalMinutes);

        // Initialize registry with existing containers on startup
        await InitializeRegistryAsync(stoppingToken);

        // Run cleanup loop
        var checkInterval = TimeSpan.FromMinutes(_config.CleanupCheckIntervalMinutes);
        var idleTimeout = TimeSpan.FromMinutes(_config.IdleTimeoutMinutes);
        var maxLifetime = TimeSpan.FromMinutes(_config.MaxContainerLifetimeMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(checkInterval, stoppingToken);

            try
            {
                // First clean up expired containers (hard limit)
                await CleanupExpiredContainersAsync(maxLifetime, stoppingToken);

                // Then clean up idle containers
                await CleanupIdleContainersAsync(idleTimeout, stoppingToken);
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

    private async Task InitializeRegistryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var containerService = scope.ServiceProvider.GetRequiredService<IKataContainerService>();

            var containers = await containerService.ListContainersAsync(cancellationToken);

            foreach (var container in containers)
            {
                var createdAt = container.CreatedAt ?? DateTime.UtcNow;
                _registry.RegisterContainer(container.Name, createdAt);
            }

            _logger.LogInformation(
                "Container registry initialized with {Count} existing containers",
                containers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize container registry on startup");
        }
    }

    private async Task CleanupExpiredContainersAsync(TimeSpan maxLifetime, CancellationToken cancellationToken)
    {
        var expiredContainers = _registry.GetExpiredContainers(maxLifetime);

        if (expiredContainers.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Found {ExpiredCount} containers that exceeded maximum lifetime of {MaxLifetimeMinutes} minutes",
            expiredContainers.Count, _config.MaxContainerLifetimeMinutes);

        using var scope = _serviceProvider.CreateScope();
        var containerService = scope.ServiceProvider.GetRequiredService<IKataContainerService>();

        foreach (var podName in expiredContainers)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var result = await containerService.DeleteContainerAsync(podName, cancellationToken);

                if (result.Success)
                {
                    _logger.LogInformation("Hard deleted expired container: {PodName} (exceeded max lifetime)", podName);
                }
                else
                {
                    _registry.UnregisterContainer(podName);
                    _logger.LogWarning(
                        "Failed to delete expired container {PodName}: {Message}. Removed from registry.",
                        podName, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting expired container: {PodName}", podName);
            }
        }
    }

    private async Task CleanupIdleContainersAsync(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var idleContainers = _registry.GetIdleContainers(idleTimeout);

        if (idleContainers.Count == 0)
        {
            _logger.LogDebug("No idle containers to clean up. Tracked containers: {Count}", _registry.Count);
            return;
        }

        _logger.LogInformation(
            "Found {IdleCount} containers idle for more than {IdleTimeoutMinutes} minutes",
            idleContainers.Count, _config.IdleTimeoutMinutes);

        using var scope = _serviceProvider.CreateScope();
        var containerService = scope.ServiceProvider.GetRequiredService<IKataContainerService>();

        foreach (var podName in idleContainers)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var result = await containerService.DeleteContainerAsync(podName, cancellationToken);

                if (result.Success)
                {
                    _logger.LogInformation("Cleaned up idle container: {PodName}", podName);
                }
                else
                {
                    // Container may have been deleted externally - remove from registry
                    _registry.UnregisterContainer(podName);
                    _logger.LogWarning(
                        "Failed to delete idle container {PodName}: {Message}. Removed from registry.",
                        podName, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up idle container: {PodName}", podName);
            }
        }
    }
}
