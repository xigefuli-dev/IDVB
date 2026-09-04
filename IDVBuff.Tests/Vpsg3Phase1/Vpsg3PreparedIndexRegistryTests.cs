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
        Assert.Equal(Vpsg3IndexStatus.Missing, registry.GetStatus(absentId, "1f"));
    }

    [Fact]
    public void PublishAndRetrieve_ProvidesValidLeaseAndFloorData()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var floor = CreateDummyFloor(mapId, "1f");

        registry.PublishFloor(floor);

        Assert.True(registry.Contains(mapId, "1f"));
        Assert.Equal(1, registry.Count);
        Assert.Equal(1, registry.ReadyCount);
        Assert.Equal(50000, registry.TotalMemoryBytes);
        Assert.Equal(Vpsg3IndexStatus.Ready, registry.GetStatus(mapId, "1f"));

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
    public void SlotLifecycle_TransitionsThroughMissingBuildingReadyFailedStale()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var key = new Vpsg3IndexCacheKey(mapId, "1f", "fp1", DateTimeOffset.UtcNow, "gen1");

        // 1. Initial status is Missing
        Assert.Equal(Vpsg3IndexStatus.Missing, registry.GetStatus(mapId, "1f"));

        // 2. BeginBuild transitions to Building
        Assert.True(registry.TryBeginBuild(key));
        Assert.Equal(Vpsg3IndexStatus.Building, registry.GetStatus(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));

        // 3. Duplicate BeginBuild with same key is rejected
        Assert.False(registry.TryBeginBuild(key));

        // 4. RecordBuildFailure transitions to Failed
        registry.RecordBuildFailure(key, "Disk decode error");
        Assert.Equal(Vpsg3IndexStatus.Failed, registry.GetStatus(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));

        // 5. Successful build and publish transitions to Ready
        var floor = CreateDummyFloor(mapId, "1f");
        registry.PublishFloor(floor);
        Assert.Equal(Vpsg3IndexStatus.Ready, registry.GetStatus(mapId, "1f"));
        Assert.True(registry.TryGet(mapId, "1f", out var lease));
        lease?.Dispose();

        // 6. Invalidation transitions to Stale
        registry.InvalidateMaps(new HashSet<Guid> { mapId });
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));
    }

    [Fact]
    public void FreshnessCheck_FullCacheKeyMismatchRejectsStaleFloor()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var time1 = DateTimeOffset.UtcNow;
        var key1 = new Vpsg3IndexCacheKey(mapId, "1f", "fp_v1", time1, "gen1", 1);
        var key2NewFingerprint = new Vpsg3IndexCacheKey(mapId, "1f", "fp_v2", time1, "gen1", 1);
        var key3NewTime = new Vpsg3IndexCacheKey(mapId, "1f", "fp_v1", time1.AddMinutes(5), "gen1", 1);
        var key4NewGen = new Vpsg3IndexCacheKey(mapId, "1f", "fp_v1", time1, "gen2", 1);
        var key5NewSchema = new Vpsg3IndexCacheKey(mapId, "1f", "fp_v1", time1, "gen1", 2);

        var floor = CreateDummyFloor(mapId, "1f", fingerprint: "fp_v1", updatedAt: time1, generation: "gen1");
        registry.PublishFloor(floor);

        // Matching exact key succeeds
        Assert.True(registry.TryGet(key1, out var lease1));
        lease1?.Dispose();

        // Any difference in ContentFingerprint, UpdatedAt, StructureGeneration, SchemaVersion rejects immediately!
        Assert.False(registry.TryGet(key2NewFingerprint, out _));
        Assert.False(registry.TryGet(key3NewTime, out _));
        Assert.False(registry.TryGet(key4NewGen, out _));
        Assert.False(registry.TryGet(key5NewSchema, out _));

        // GetStatus reports Stale when compared to newer expected key
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(mapId, "1f", key2NewFingerprint));
    }

    [Fact]
    public void InvalidateMaps_RemovesSpecifiedMapAndPreservesOthers()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var map1 = Guid.NewGuid();
        var map2 = Guid.NewGuid();

        registry.PublishFloor(CreateDummyFloor(map1, "1f"));
        registry.PublishFloor(CreateDummyFloor(map1, "2f"));
        registry.PublishFloor(CreateDummyFloor(map2, "1f"));

        Assert.Equal(3, registry.ReadyCount);

        registry.InvalidateMaps(new HashSet<Guid> { map1 });

        Assert.Equal(1, registry.ReadyCount);
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
        registry.PublishFloor(originalFloor);

        // 1. Check out lease
        var got = registry.TryGet(mapId, "1f", out var lease);
        Assert.True(got);
        Assert.NotNull(lease);

        // 2. Invalidate map from registry while lease is active
        registry.InvalidateMaps(new HashSet<Guid> { mapId });

        // Registry slot is no longer ready
        Assert.False(registry.Contains(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));

        // 3. The checked-out lease MUST still be alive and usable
        Assert.False(lease.Floor.IsDisposed);
        Assert.False(lease.Floor.DilatedBitsetSpan.IsEmpty);
        Assert.Equal(800, lease.Floor.ReferenceWidth);

        // 4. Dispose lease -> ref count drops to 0, floor is cleaned up
        lease.Dispose();
        Assert.True(originalFloor.IsDisposed);
        Assert.True(originalFloor.DilatedBitsetSpan.IsEmpty);
    }

    [Fact]
    public void CasRetain_ZeroToOneResurrection_IsStrictlyPrevented()
    {
        var floor = CreateDummyFloor(Guid.NewGuid(), "1f");

        // Release the initial 1 count -> refCount reaches 0 and triggers cleanup
        floor.Dispose();
        Assert.True(floor.IsDisposed);

        // Calling TryRetain on a 0-ref floor MUST return false and NEVER resurrect
        var retained = floor.TryRetain();
        Assert.False(retained);
        Assert.True(floor.IsDisposed);

        // Lease factory must reject 0-ref floor
        var leaseCreated = Vpsg3FloorIndexLease.TryCreate(floor, out var lease);
        Assert.False(leaseCreated);
        Assert.Null(lease);
    }

    [Fact]
    public async Task CasRetain_ConcurrentReleaseAndRetainRace_PreventsResurrection()
    {
        // Deterministic concurrent race between releasing the final reference and attempting new retains
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var floor = CreateDummyFloor(Guid.NewGuid(), "1f");

            var startBarrier = new Barrier(2);
            var retainSuccess = false;

            var releaseTask = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                floor.Release();
            });

            var retainTask = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                retainSuccess = floor.TryRetain();
                if (retainSuccess)
                {
                    floor.Release();
                }
            });

            await Task.WhenAll(releaseTask, retainTask);

            // Once both tasks finish, if retain succeeded, it also released.
            // In either case, the floor MUST be cleanly disposed and cannot be retained now!
            Assert.False(floor.TryRetain());
            Assert.True(floor.IsDisposed);
        }
    }

    [Fact]
    public void RegistryDispose_ActiveLeaseRemainsValidUntilCallerDisposes()
    {
        var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var floor = CreateDummyFloor(mapId, "1f");
        registry.PublishFloor(floor);

        Assert.True(registry.TryGet(mapId, "1f", out var lease));
        Assert.NotNull(lease);

        // Dispose entire registry
        registry.Dispose();
        Assert.False(registry.TryGet(mapId, "1f", out _));

        // Lease floor must still be alive and readable
        Assert.False(lease.Floor.IsDisposed);
        Assert.Equal(800, lease.Floor.ReferenceWidth);

        // Dispose lease
        lease.Dispose();
        Assert.True(floor.IsDisposed);
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
            registry.PublishFloor(CreateDummyFloor(id, "1f"));
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
                registry.PublishFloor(CreateDummyFloor(id, "1f"));
                Thread.Sleep(5);
            }
        })).ToArray();

        await Task.WhenAll([.. readTasks, .. writeTasks]);
    }
}
