using DonkeyWork.CodeSandbox.Manager.Models;

namespace DonkeyWork.CodeSandbox.Manager.Services;

public interface IPoolManager
{
    /// <summary>
    /// Allocates a warm sandbox from the pool to a user.
    /// </summary>
    /// <param name="userId">The user ID to allocate the sandbox to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The allocated container info, or null if no warm sandboxes available.</returns>
    Task<KataContainerInfo?> AllocateWarmSandboxAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current count of warm (unallocated) sandboxes in the pool.
    /// </summary>
    Task<int> GetWarmPoolCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current count of allocated sandboxes.
    /// </summary>
    Task<int> GetAllocatedCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates warm sandboxes to fill the pool up to the target size.
    /// Only should be called by the leader.
    /// </summary>
    Task BackfillPoolAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Monitors pods for Failed/Succeeded states and handles cleanup/recreation.
    /// </summary>
    Task MonitorAndCleanupFailedPodsAsync(CancellationToken cancellationToken = default);
}
