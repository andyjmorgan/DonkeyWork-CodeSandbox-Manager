using DonkeyWork.CodeSandbox.Manager.Configuration;
using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services;

/// <summary>
/// Background service that uses Kubernetes leader election to ensure only one manager
/// performs pool backfilling at a time.
/// </summary>
public class PoolBackfillService : BackgroundService
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<PoolBackfillService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly string _identity;
    private CancellationTokenSource? _leaderLoopCts;

    public PoolBackfillService(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<PoolBackfillService> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _identity = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PoolBackfillService starting with identity: {Identity}", _identity);

        // Create the lease-based resource lock
        var resourceLock = new LeaseLock(
            _client,
            _config.TargetNamespace,
            "pool-backfill-leader",
            _identity);

        // Configure leader election
        var electionConfig = new LeaderElectionConfig(resourceLock)
        {
            LeaseDuration = TimeSpan.FromSeconds(_config.LeaderLeaseDurationSeconds),
            RetryPeriod = TimeSpan.FromSeconds(_config.LeaderLeaseDurationSeconds / 3),
            RenewDeadline = TimeSpan.FromSeconds(_config.LeaderLeaseDurationSeconds * 2 / 3)
        };

        // Run leader election
        var leaderElector = new LeaderElector(electionConfig);

        leaderElector.OnStartedLeading += () =>
        {
            _logger.LogInformation("🎖️  I am now the LEADER for pool backfill");
            _leaderLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _ = Task.Run(() => BackfillLoop(_leaderLoopCts.Token), stoppingToken);
        };

        leaderElector.OnStoppedLeading += () =>
        {
            _logger.LogWarning("❌ Lost leadership for pool backfill");
            _leaderLoopCts?.Cancel();
        };

        leaderElector.OnNewLeader += (newLeader) =>
        {
            if (newLeader != _identity)
            {
                _logger.LogInformation("New leader elected: {Leader}", newLeader);
            }
        };

        try
        {
            await leaderElector.RunUntilLeadershipLostAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PoolBackfillService stopping gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoolBackfillService encountered an error");
            throw;
        }
    }

    private async Task BackfillLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting backfill loop (running every {Interval}s)",
            _config.PoolBackfillCheckIntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Use a scoped service to get IPoolManager
                using var scope = _serviceScopeFactory.CreateScope();
                var poolManager = scope.ServiceProvider.GetRequiredService<IPoolManager>();

                await poolManager.BackfillPoolAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in backfill loop iteration");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.PoolBackfillCheckIntervalSeconds),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Backfill loop cancelled");
                break;
            }
        }

        _logger.LogInformation("Backfill loop stopped");
    }
}
