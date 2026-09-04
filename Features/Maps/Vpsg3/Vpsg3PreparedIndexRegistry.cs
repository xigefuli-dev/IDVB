using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Application-lifetime, thread-safe registry holding prepared reference floor indices for VPSG 3.0.
/// All queries are strictly non-blocking: missing, building, failed, or stale indices return false immediately.
/// Swaps are atomic, full CacheKey validation guards freshness, and active leases protect against concurrent eviction.
/// </summary>
public sealed class Vpsg3PreparedIndexRegistry : IVpsg3PreparedIndexRegistry
{
    private ImmutableDictionary<(Guid MapId, string FloorKey), Vpsg3FloorSlot> _slots =
        ImmutableDictionary<(Guid MapId, string FloorKey), Vpsg3FloorSlot>.Empty;

    private int _disposed;

    public int Count => _slots.Count;

    public int ReadyCount => _slots.Values.Count(s => s.IsReady);

    public long TotalMemoryBytes
    {
        get
        {
            var snapshot = _slots;
            var sum = 0L;
            foreach (var slot in snapshot.Values)
            {
                if (slot.Floor is { IsDisposed: false } floor)
                {
                    sum += floor.MemoryBytes;
                }
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
        var snapshot = _slots;
        if (!snapshot.TryGetValue((mapId, normalizedFloor), out var slot)
            || slot.Status != Vpsg3IndexStatus.Ready
            || slot.Floor is null
            || slot.Floor.IsDisposed)
        {
            lease = null;
            return false;
        }

        return Vpsg3FloorIndexLease.TryCreate(slot.Floor, out lease);
    }

    /// <inheritdoc />
    public bool TryGet(
        Vpsg3IndexCacheKey expectedKey,
        [NotNullWhen(true)] out Vpsg3FloorIndexLease? lease)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            lease = null;
            return false;
        }

        var normalizedFloor = expectedKey.NormalizeFloorKey();
        var snapshot = _slots;
        if (!snapshot.TryGetValue((expectedKey.MapId, normalizedFloor), out var slot)
            || slot.Status != Vpsg3IndexStatus.Ready
            || slot.Floor is null
            || slot.Floor.IsDisposed)
        {
            lease = null;
            return false;
        }

        // Strict freshness verification: all dimensions of the cache key must match
        if (slot.Floor.CacheKey != expectedKey)
        {
            lease = null;
            return false;
        }

        return Vpsg3FloorIndexLease.TryCreate(slot.Floor, out lease);
    }

    /// <inheritdoc />
    public Vpsg3IndexStatus GetStatus(Guid mapId, string floorKey, Vpsg3IndexCacheKey? expectedKey = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Vpsg3IndexStatus.Missing;

        var normalizedFloor = NormalizeFloor(floorKey);
        if (!_slots.TryGetValue((mapId, normalizedFloor), out var slot))
            return Vpsg3IndexStatus.Missing;

        if (expectedKey is not null && slot.ExpectedKey != expectedKey.Value)
            return Vpsg3IndexStatus.Stale;

        return slot.Status;
    }

    /// <inheritdoc />
    public bool TryBeginBuild(Vpsg3IndexCacheKey expectedKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var key = (expectedKey.MapId, expectedKey.NormalizeFloorKey());

        while (true)
        {
            var current = _slots;
            if (current.TryGetValue(key, out var existing))
            {
                if (existing.ExpectedKey == expectedKey
                    && (existing.Status == Vpsg3IndexStatus.Ready || existing.Status == Vpsg3IndexStatus.Building))
                {
                    // Already ready or actively building for the exact same key
                    return false;
                }
            }

            var updatedSlot = new Vpsg3FloorSlot(expectedKey, Vpsg3IndexStatus.Building);
            var updatedDict = current.SetItem(key, updatedSlot);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updatedDict, current), current))
                return true;
        }
    }

    /// <inheritdoc />
    public bool TryPublishFloor(Vpsg3IndexCacheKey expectedKey, Vpsg3PreparedFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);

        if (floor.CacheKey != expectedKey || Volatile.Read(ref _disposed) != 0)
        {
            floor.Dispose();
            return false;
        }

        var key = (expectedKey.MapId, expectedKey.NormalizeFloorKey());
        Vpsg3PreparedFloor? displacedFloor = null;

        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                floor.Dispose();
                return false;
            }

            var current = _slots;
            if (!current.TryGetValue(key, out var existing))
            {
                // Slot does not exist, reject stale build
                floor.Dispose();
                return false;
            }

            // Only transition to Ready if the slot is currently Building and its expected key matches
            if (existing.Status != Vpsg3IndexStatus.Building || existing.ExpectedKey != expectedKey)
            {
                // Stale background build or superseded slot: reject and dispose floor
                floor.Dispose();
                return false;
            }

            displacedFloor = existing.Floor;
            var updatedSlot = new Vpsg3FloorSlot(
                expectedKey,
                Vpsg3IndexStatus.Ready,
                floor: floor);

            var updatedDict = current.SetItem(key, updatedSlot);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updatedDict, current), current))
            {
                displacedFloor?.Dispose();
                return true;
            }
        }
    }

    /// <inheritdoc />
    public bool RecordBuildFailure(Vpsg3IndexCacheKey expectedKey, string failureReason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var key = (expectedKey.MapId, expectedKey.NormalizeFloorKey());
        Vpsg3PreparedFloor? displacedFloor = null;

        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            var current = _slots;
            if (!current.TryGetValue(key, out var existing))
                return false;

            // Only mark as Failed if still Building for this exact expectedKey
            if (existing.Status != Vpsg3IndexStatus.Building || existing.ExpectedKey != expectedKey)
                return false;

            displacedFloor = existing.Floor;
            var updatedSlot = new Vpsg3FloorSlot(
                expectedKey,
                Vpsg3IndexStatus.Failed,
                failureReason: failureReason);

            var updatedDict = current.SetItem(key, updatedSlot);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updatedDict, current), current))
            {
                displacedFloor?.Dispose();
                return true;
            }
        }
    }

    /// <inheritdoc />
    public bool Contains(Guid mapId, string floorKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var normalizedFloor = NormalizeFloor(floorKey);
        return _slots.TryGetValue((mapId, normalizedFloor), out var slot) && slot.IsReady;
    }

    /// <inheritdoc />
    public void InvalidateMaps(IReadOnlySet<Guid> mapIds)
    {
        if (mapIds is null || mapIds.Count == 0 || Volatile.Read(ref _disposed) != 0)
            return;

        List<Vpsg3PreparedFloor>? displacedFloors = null;

        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var current = _slots;
            var builder = current.ToBuilder();
            displacedFloors?.Clear();
            displacedFloors ??= new List<Vpsg3PreparedFloor>();
            var matchedAnySlot = false;

            foreach (var (key, slot) in current)
            {
                if (mapIds.Contains(key.MapId))
                {
                    matchedAnySlot = true;
                    // Transition to Stale if not already (Stale && Floor == null)
                    if (slot.Status != Vpsg3IndexStatus.Stale || slot.Floor is not null)
                    {
                        var staleSlot = new Vpsg3FloorSlot(slot.ExpectedKey, Vpsg3IndexStatus.Stale);
                        builder[key] = staleSlot;
                        if (slot.Floor is not null)
                            displacedFloors.Add(slot.Floor);
                    }
                }
            }

            if (!matchedAnySlot)
                return;

            var updated = builder.ToImmutable();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updated, current), current))
                break;
        }

        foreach (var floor in displacedFloors)
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
        Vpsg3PreparedFloor? displaced = null;

        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var current = _slots;
            if (!current.TryGetValue(key, out var existing))
                return;

            if (existing.Status == Vpsg3IndexStatus.Stale && existing.Floor is null)
                return;

            displaced = existing.Floor;
            var staleSlot = new Vpsg3FloorSlot(existing.ExpectedKey, Vpsg3IndexStatus.Stale);
            var updated = current.SetItem(key, staleSlot);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updated, current), current))
                break;
        }

        displaced?.Dispose();
    }

    /// <inheritdoc />
    public void Clear()
    {
        var previous = Interlocked.Exchange(
            ref _slots,
            ImmutableDictionary<(Guid MapId, string FloorKey), Vpsg3FloorSlot>.Empty);

        foreach (var slot in previous.Values)
        {
            slot.Floor?.Dispose();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Vpsg3IndexCacheKey> GetActiveKeys() =>
        _slots.Values.Where(s => s.IsReady && s.Floor is not null).Select(s => s.Floor!.CacheKey).ToArray();

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
