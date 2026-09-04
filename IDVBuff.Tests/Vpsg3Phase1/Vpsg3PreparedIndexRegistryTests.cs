using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class Vpsg3PreparedIndexRegistryTests
{
    private static Vpsg3PreparedFloor CreateDummyFloor(
        Guid mapId,
        string floorKey = "1f",
        string fingerprint = "fp123",
        DateTimeOffset? updatedAt = null,
        string generation = "gen1",
        int width = 800,
        int height = 600)
    {
        var key = new Vpsg3IndexCacheKey(
            mapId,
            floorKey,
            fingerprint,
            updatedAt ?? DateTimeOffset.UtcNow,
            generation,
            SchemaVersion: 1);

        var wordsPerRow = (width + 63) / 64;
        var bitset = new ulong[height * wordsPerRow];
        var scalePrior = new Vpsg3ScalePrior(
            SeedScale: 1.0d,
            PeakRatio: 2.5d,
            FastPathEligible: true,
            RejectReason: string.Empty,
            ReferencePitch: 50.0d,
            ReferencePeakRatio: 2.5d);

        return new Vpsg3PreparedFloor(
            key,
            width,
            height,
            edgePixelCount: 1500,
            scalePrior,
            wordsPerRow,
            bitset,
            memoryBytes: 50000);
    }

    [Fact]
    public void TryGet_NonBlocking_ReturnsFalseWhenAbsent()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var absentId = Guid.NewGuid();

        var success = registry.TryGet(absentId, "1f", out var lease);

        Assert.False(success);
        Assert.Null(lease);
    }

    [Fact]
    public void RegisterAndRetrieve_ProvidesValidLeaseAndFloorData()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var floor = CreateDummyFloor(mapId, "1f");

        registry.RegisterFloor(floor);

        Assert.True(registry.Contains(mapId, "1f"));
        Assert.Equal(1, registry.Count);
        Assert.Equal(50000, registry.TotalMemoryBytes);

        var got = registry.TryGet(mapId, "1f", out var lease);
        Assert.True(got);
        Assert.NotNull(lease);

        using (lease)
        {
            Assert.Equal(mapId, lease.Floor.CacheKey.MapId);
            Assert.Equal(800, lease.Floor.ReferenceWidth);
            Assert.Equal(600, lease.Floor.ReferenceHeight);
            Assert.False(lease.Floor.IsDisposed);
        }
    }

    [Fact]
    public void InvalidateMaps_RemovesSpecifiedMapAndPreservesOthers()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var map1 = Guid.NewGuid();
        var map2 = Guid.NewGuid();

        registry.RegisterFloor(CreateDummyFloor(map1, "1f"));
        registry.RegisterFloor(CreateDummyFloor(map1, "2f"));
        registry.RegisterFloor(CreateDummyFloor(map2, "1f"));

        Assert.Equal(3, registry.Count);

        registry.InvalidateMaps(new HashSet<Guid> { map1 });

        Assert.Equal(1, registry.Count);
        Assert.False(registry.Contains(map1, "1f"));
        Assert.False(registry.Contains(map1, "2f"));
        Assert.True(registry.Contains(map2, "1f"));
    }

    [Fact]
    public void DelayedDisposal_ActiveLeaseRemainsUsableAfterEvictionUntilDisposed()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var originalFloor = CreateDummyFloor(mapId, "1f");
        registry.RegisterFloor(originalFloor);

        // 1. Check out lease
        var got = registry.TryGet(mapId, "1f", out var lease);
        Assert.True(got);
        Assert.NotNull(lease);

        // 2. Invalidate map from registry while lease is active
        registry.InvalidateMaps(new HashSet<Guid> { mapId });

        // Registry no longer contains the floor
        Assert.False(registry.Contains(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));

        // 3. The checked-out lease MUST still be alive and usable
        Assert.False(lease.Floor.IsDisposed);
        Assert.NotNull(lease.Floor.UnsafeDilatedBitset);
        Assert.Equal(800, lease.Floor.ReferenceWidth);

        // 4. Dispose lease -> ref count drops to 0, floor is cleaned up
        lease.Dispose();
        Assert.True(originalFloor.IsDisposed);
        Assert.Null(originalFloor.UnsafeDilatedBitset);
    }

    [Fact]
    public void CacheKey_DistinguishesAllDimensions()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var k1 = new Vpsg3IndexCacheKey(id1, "1f", "fp1", now, "gen1", 1);
        var kSame = new Vpsg3IndexCacheKey(id1, "1f", "fp1", now, "gen1", 1);
        var kDiffMap = new Vpsg3IndexCacheKey(id2, "1f", "fp1", now, "gen1", 1);
        var kDiffFloor = new Vpsg3IndexCacheKey(id1, "2f", "fp1", now, "gen1", 1);
        var kDiffFp = new Vpsg3IndexCacheKey(id1, "1f", "fp2", now, "gen1", 1);
        var kDiffTime = new Vpsg3IndexCacheKey(id1, "1f", "fp1", now.AddMinutes(1), "gen1", 1);
        var kDiffGen = new Vpsg3IndexCacheKey(id1, "1f", "fp1", now, "gen2", 1);
        var kDiffVer = new Vpsg3IndexCacheKey(id1, "1f", "fp1", now, "gen1", 2);

        Assert.Equal(k1, kSame);
        Assert.Equal(k1.GetHashCode(), kSame.GetHashCode());

        Assert.NotEqual(k1, kDiffMap);
        Assert.NotEqual(k1, kDiffFloor);
        Assert.NotEqual(k1, kDiffFp);
        Assert.NotEqual(k1, kDiffTime);
        Assert.NotEqual(k1, kDiffGen);
        Assert.NotEqual(k1, kDiffVer);
    }

    [Fact]
    public async Task ConcurrentAccess_DoesNotDeadlockOrThrow()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in mapIds)
        {
            registry.RegisterFloor(CreateDummyFloor(id, "1f"));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;

        var readTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var rnd = new Random();
            while (!token.IsCancellationRequested)
            {
                var id = mapIds[rnd.Next(mapIds.Length)];
                if (registry.TryGet(id, "1f", out var lease))
                {
                    using (lease)
                    {
                        var w = lease.Floor.ReferenceWidth;
                        Assert.True(w > 0);
                    }
                }
            }
        })).ToArray();

        var writeTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var rnd = new Random();
            while (!token.IsCancellationRequested)
            {
                var id = mapIds[rnd.Next(mapIds.Length)];
                registry.RegisterFloor(CreateDummyFloor(id, "1f"));
                Thread.Sleep(5);
            }
        })).ToArray();

        await Task.WhenAll([.. readTasks, .. writeTasks]);
    }
}
