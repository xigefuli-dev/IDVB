using System.Diagnostics.CodeAnalysis;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Application-scoped, thread-safe registry holding prepared reference floor indices for VPSG 3.0.
/// All queries are strictly non-blocking: missing, building, or stale indices return false immediately.
/// </summary>
public interface IVpsg3PreparedIndexRegistry : IDisposable
{
    /// <summary>
    /// Attempts to obtain a non-blocking lease on the prepared index for the specified floor.
    /// Returns false immediately without waiting or performing synchronous I/O if the index is absent or stale.
    /// </summary>
    bool TryGet(Guid mapId, string floorKey, [NotNullWhen(true)] out Vpsg3FloorIndexLease? lease);

    /// <summary>
    /// Checks whether an index exists and is ready for the specified floor.
    /// </summary>
    bool Contains(Guid mapId, string floorKey);

    /// <summary>
    /// Atomically registers or updates a prepared floor index.
    /// </summary>
    void RegisterFloor(Vpsg3PreparedFloor floor);

    /// <summary>
    /// Invalidates and removes all prepared floors associated with the specified map IDs.
    /// Old floors are scheduled for delayed disposal once existing leases are released.
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

    /// <summary>
    /// Total count of registered floors.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Total estimated resident memory in bytes across all prepared floor indices.
    /// </summary>
    long TotalMemoryBytes { get; }

    /// <summary>
    /// Returns a snapshot of all registered cache keys.
    /// </summary>
    IReadOnlyList<Vpsg3IndexCacheKey> GetActiveKeys();
}
