namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Detailed statistics about the warm sandbox pool.
/// </summary>
public class PoolStatistics
{
    /// <summary>
    /// Number of sandboxes currently being created (not yet ready).
    /// </summary>
    public int Creating { get; init; }

    /// <summary>
    /// Number of sandboxes ready for allocation (warm and available).
    /// </summary>
    public int Warm { get; init; }

    /// <summary>
    /// Number of sandboxes currently allocated to users.
    /// </summary>
    public int Allocated { get; init; }

    /// <summary>
    /// Total number of sandboxes in the pool (creating + warm + allocated).
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// Target pool size configured.
    /// </summary>
    public int TargetSize { get; init; }

    /// <summary>
    /// Percentage of pool that is ready (warm / targetSize * 100).
    /// </summary>
    public double ReadyPercentage { get; init; }

    /// <summary>
    /// Percentage of pool that is allocated (allocated / total * 100).
    /// </summary>
    public double UtilizationPercentage { get; init; }
}
