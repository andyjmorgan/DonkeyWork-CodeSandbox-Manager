namespace DonkeyWork.CodeSandbox.Manager.Services;

/// <summary>
/// Thread-safe singleton registry for tracking container activity.
/// </summary>
public interface IContainerRegistry
{
    /// <summary>
    /// Register a container when it is created.
    /// </summary>
    void RegisterContainer(string podName, DateTime createdAt);

    /// <summary>
    /// Unregister a container when it is deleted.
    /// </summary>
    void UnregisterContainer(string podName);

    /// <summary>
    /// Update the last activity time for a container (e.g., when a command is executed).
    /// </summary>
    void UpdateLastActivity(string podName);

    /// <summary>
    /// Get the last activity time for a container.
    /// </summary>
    DateTime? GetLastActivity(string podName);

    /// <summary>
    /// Get all containers that have been idle longer than the specified threshold.
    /// </summary>
    IReadOnlyList<string> GetIdleContainers(TimeSpan idleThreshold);

    /// <summary>
    /// Get all containers that have exceeded their maximum lifetime.
    /// </summary>
    IReadOnlyList<string> GetExpiredContainers(TimeSpan maxLifetime);

    /// <summary>
    /// Get the count of tracked containers.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Get all tracked containers with their tracking info for debugging.
    /// </summary>
    IReadOnlyDictionary<string, (DateTime CreatedAt, DateTime LastActivity)> GetAllContainers();
}
