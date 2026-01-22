using DonkeyWork.CodeSandbox.Manager.Configuration;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services;

/// <summary>
/// Background service that monitors pods for Failed/Succeeded states and handles cleanup.
/// Runs on all manager instances (no leader election needed).
/// </summary>
public class PoolMonitoringService : BackgroundService
{
    private readonly ILogger<PoolMonitoringService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly KataContainerManager _config;

    public PoolMonitoringService(
        ILogger<PoolMonitoringService> logger,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<KataContainerManager> config)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PoolMonitoringService starting");

        // Wait a bit before starting to let the system stabilize
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var poolManager = scope.ServiceProvider.GetRequiredService<IPoolManager>();

                await poolManager.MonitorAndCleanupFailedPodsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring loop iteration");
            }

            try
            {
                // Check more frequently than backfill to catch failures quickly
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Monitoring loop cancelled");
                break;
            }
        }

        _logger.LogInformation("PoolMonitoringService stopped");
    }
}
