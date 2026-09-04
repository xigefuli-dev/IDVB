using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Application-lifetime, thread-safe registry holding prepared reference floor indices for VPSG 3.0.
/// All queries are strictly non-blocking: missing, building, or stale indices return false immediately.
/// Swaps are atomic and leases prevent concurrent reads from accessing disposed memory.
/// </summary>
public sealed class Vpsg3PreparedIndexRegistry : IVpsg3PreparedIndexRegistry
{
    private ImmutableDictionary<(Guid MapId, string FloorKey), Vpsg3PreparedFloor> _floors =
        ImmutableDictionary<(Guid MapId, string FloorKey), Vpsg3PreparedFloor>.Empty;

    private int _disposed;

    public int Count => _floors.Count;

    public long TotalMemoryBytes
    {
        get
        {
            var snapshot = _floors;
            var sum = 0L;
            foreach (var floor in snapshot.Values)
            {
                sum += floor.MemoryBytes;
            }
            return sum;
        }
    }

    /// <inheritdoc />
    public bool TryGet(
        Guid mapId,
        string floorKey,
        [NotNullWhen(true)] out Vpsg3FloorIndexLease? lease)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            lease = null;
            return false;
        }

        var normalizedFloor = NormalizeFloor(floorKey);
        var snapshot = _floors;
        if (!snapshot.TryGetValue((mapId, normalizedFloor), out var floor) || floor.IsDisposed)
        {
            lease = null;
            return false;
        }

        try
        {
            lease = new Vpsg3FloorIndexLease(floor);
            return true;
        }
        catch (ObjectDisposedException)
        {
            lease = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool Contains(Guid mapId, string floorKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var normalizedFloor = NormalizeFloor(floorKey);
        return _floors.TryGetValue((mapId, normalizedFloor), out var floor) && !floor.IsDisposed;
    }

    /// <inheritdoc />
    public void RegisterFloor(Vpsg3PreparedFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = (floor.CacheKey.MapId, floor.CacheKey.NormalizeFloorKey());
        Vpsg3PreparedFloor? displaced = null;

        while (true)
        {
            var current = _floors;
            current.TryGetValue(key, out displaced);
            var updated = current.SetItem(key, floor);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _floors, updated, current), current))
                break;
        }

        displaced?.Dispose();
    }

    /// <inheritdoc />
    public void InvalidateMaps(IReadOnlySet<Guid> mapIds)
    {
        if (mapIds is null || mapIds.Count == 0 || Volatile.Read(ref _disposed) != 0)
            return;

        List<Vpsg3PreparedFloor>? removedFloors = null;

        while (true)
        {
            var current = _floors;
            var builder = current.ToBuilder();
            removedFloors?.Clear();
            removedFloors ??= new List<Vpsg3PreparedFloor>();

            foreach (var (key, floor) in current)
            {
                if (mapIds.Contains(key.MapId))
                {
                    builder.Remove(key);
                    removedFloors.Add(floor);
                }
            }

            if (removedFloors.Count == 0)
                return;

            var updated = builder.ToImmutable();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _floors, updated, current), current))
                break;
        }

        foreach (var floor in removedFloors)
        {
            floor.Dispose();
        }
    }

    /// <inheritdoc />
    public void InvalidateFloor(Guid mapId, string floorKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var key = (mapId, NormalizeFloor(floorKey));
        Vpsg3PreparedFloor? removed = null;

        while (true)
        {
            var current = _floors;
            if (!current.TryGetValue(key, out removed))
                return;

            var updated = current.Remove(key);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _floors, updated, current), current))
                break;
        }

        removed?.Dispose();
    }

    /// <inheritdoc />
    public void Clear()
    {
        var previous = Interlocked.Exchange(
            ref _floors,
            ImmutableDictionary<(Guid MapId, string FloorKey), Vpsg3PreparedFloor>.Empty);

        foreach (var floor in previous.Values)
        {
            floor.Dispose();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Vpsg3IndexCacheKey> GetActiveKeys() =>
        _floors.Values.Select(f => f.CacheKey).ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Clear();
        }
    }

    private static string NormalizeFloor(string? floor) =>
        string.IsNullOrWhiteSpace(floor) ? string.Empty : floor.Trim().ToLowerInvariant();
}
