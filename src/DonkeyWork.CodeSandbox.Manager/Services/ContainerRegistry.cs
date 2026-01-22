using System.Collections.Concurrent;

namespace DonkeyWork.CodeSandbox.Manager.Services;

/// <summary>
/// Tracks both creation time and last activity time for a container.
/// </summary>
internal record ContainerTrackingInfo(DateTime CreatedAt, DateTime LastActivity);

/// <summary>
/// Thread-safe singleton registry for tracking container activity times.
/// </summary>
public class ContainerRegistry : IContainerRegistry
{
    private readonly ConcurrentDictionary<string, ContainerTrackingInfo> _containers = new();
    private readonly ILogger<ContainerRegistry> _logger;

    public ContainerRegistry(ILogger<ContainerRegistry> logger)
    {
        _logger = logger;
    }

    public int Count => _containers.Count;

    public void RegisterContainer(string podName, DateTime createdAt)
    {
        _containers[podName] = new ContainerTrackingInfo(createdAt, createdAt);
        _logger.LogDebug("Registered container {PodName} with creation time {CreatedAt}", podName, createdAt);
    }

    public void UnregisterContainer(string podName)
    {
        if (_containers.TryRemove(podName, out _))
        {
            _logger.LogDebug("Unregistered container {PodName}", podName);
        }
    }

    public void UpdateLastActivity(string podName)
    {
        var now = DateTime.UtcNow;
        _containers.AddOrUpdate(
            podName,
            new ContainerTrackingInfo(now, now),
            (_, existing) => existing with { LastActivity = now });
        _logger.LogDebug("Updated last activity for container {PodName}", podName);
    }

    public DateTime? GetLastActivity(string podName)
    {
        return _containers.TryGetValue(podName, out var info) ? info.LastActivity : null;
    }

    public IReadOnlyList<string> GetIdleContainers(TimeSpan idleThreshold)
    {
        var cutoff = DateTime.UtcNow - idleThreshold;
        return _containers
            .Where(kvp => kvp.Value.LastActivity < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    public IReadOnlyList<string> GetExpiredContainers(TimeSpan maxLifetime)
    {
        var cutoff = DateTime.UtcNow - maxLifetime;
        return _containers
            .Where(kvp => kvp.Value.CreatedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
    }
}
