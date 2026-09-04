using System.Diagnostics.CodeAnalysis;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Application-scoped, thread-safe registry holding prepared reference floor indices for VPSG 3.0.
/// All queries are strictly non-blocking: missing, building, failed, or stale indices return false immediately.
/// </summary>
public interface IVpsg3PreparedIndexRegistry : IDisposable
{
    /// <summary>
    /// Attempts to obtain a non-blocking lease on the prepared index for the specified floor slot.
    /// Returns false immediately if missing, building, failed, or stale.
    /// </summary>
    bool TryGet(Guid mapId, string floorKey, [NotNullWhen(true)] out Vpsg3FloorIndexLease? lease);

    /// <summary>
    /// Attempts to obtain a non-blocking lease matching the expected full CacheKey.
    /// Returns false immediately if absent, building, failed, or if slot content is stale relative to expectedKey.
    /// </summary>
    bool TryGet(Vpsg3IndexCacheKey expectedKey, [NotNullWhen(true)] out Vpsg3FloorIndexLease? lease);

    /// <summary>
    /// Gets the current status of the specified floor slot.
    /// </summary>
    Vpsg3IndexStatus GetStatus(Guid mapId, string floorKey, Vpsg3IndexCacheKey? expectedKey = null);

    /// <summary>
    /// Atomically transitions the slot to Building state for the given expected key.
    /// Returns false if already Building or Ready with the exact same expected key.
    /// </summary>
    bool TryBeginBuild(Vpsg3IndexCacheKey expectedKey);

    /// <summary>
    /// Atomically publishes a successfully built immutable prepared floor into its slot.
    /// Replaces old floor (which is scheduled for disposal once active leases release).
    /// </summary>
    void PublishFloor(Vpsg3PreparedFloor floor);

    /// <summary>
    /// Atomically marks a slot as Failed with an error reason.
    /// </summary>
    void RecordBuildFailure(Vpsg3IndexCacheKey expectedKey, string failureReason);

    /// <summary>
    /// Checks whether an index exists and is ready for the specified floor.
    /// </summary>
    bool Contains(Guid mapId, string floorKey);

    /// <summary>
    /// Synchronously invalidates all prepared floors associated with the specified map IDs,
    /// transitioning their slots to Stale and scheduling old floors for delayed disposal.
    /// </summary>
    void InvalidateMaps(IReadOnlySet<Guid> mapIds);

    /// <summary>
    /// Invalidates and removes the specified floor for a map.
    /// </summary>
    void InvalidateFloor(Guid mapId, string floorKey);

    /// <summary>
    /// Removes and disposes all registered indices.
    /// </summary>
    void Clear();

    /// <summary>Total count of tracked slots.</summary>
    int Count { get; }

    /// <summary>Count of floors currently in Ready state.</summary>
    int ReadyCount { get; }

    /// <summary>Total estimated resident memory in bytes across all prepared floor indices.</summary>
    long TotalMemoryBytes { get; }

    /// <summary>Returns a snapshot of all active/ready cache keys.</summary>
    IReadOnlyList<Vpsg3IndexCacheKey> GetActiveKeys();
}
