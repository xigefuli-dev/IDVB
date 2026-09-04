using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class Vpsg3PreparedIndexRegistryTests
{
    private static Vpsg3PreparedFloor CreateDummyFloor(
        Guid mapId,
        string floorKey,
        int width = 800,
        int height = 600,
        string fingerprint = "dummy_fp",
        DateTimeOffset? updatedAt = null,
        string generation = "gen1",
        int schemaVersion = 1)
    {
        var key = new Vpsg3IndexCacheKey(
            mapId,
            floorKey,
            fingerprint,
            updatedAt ?? DateTimeOffset.UtcNow,
            generation,
            schemaVersion);

        var wordsPerRow = (width + 63) / 64;
        var bitset = new ulong[height * wordsPerRow];
        bitset[0] = 0x123456789ABCDEF0UL;

        return new Vpsg3PreparedFloor(
            key,
            width,
            height,
            edgePixelCount: 1500,
            scalePrior: new Vpsg3ScalePrior(1.0, 3.5, true, string.Empty, 32.0, 3.5),
            wordsPerRow,
            bitset,
            memoryBytes: 50000);
    }

    private static void PublishToRegistry(Vpsg3PreparedIndexRegistry registry, Vpsg3PreparedFloor floor)
    {
        Assert.True(registry.TryBeginBuild(floor.CacheKey));
        Assert.True(registry.TryPublishFloor(floor.CacheKey, floor));
    }

    [Fact]
    public void InitialRegistry_IsEmpty_AndNonBlockingQueriesReturnFalse()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var absentId = Guid.NewGuid();

        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.ReadyCount);
        Assert.Equal(0, registry.TotalMemoryBytes);
        Assert.False(registry.Contains(absentId, "1f"));

        var got = registry.TryGet(absentId, "1f", out var lease);
        Assert.False(got);
        Assert.Null(lease);
        Assert.Equal(Vpsg3IndexStatus.Missing, registry.GetStatus(absentId, "1f"));
    }

    [Fact]
    public void PublishAndRetrieve_ProvidesValidLeaseAndFloorData()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var floor = CreateDummyFloor(mapId, "1f");

        PublishToRegistry(registry, floor);

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
        Assert.True(registry.RecordBuildFailure(key, "Disk decode error"));
        Assert.Equal(Vpsg3IndexStatus.Failed, registry.GetStatus(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));

        // 5. Successful build and publish transitions to Ready
        Assert.True(registry.TryBeginBuild(key));
        var floor = CreateDummyFloor(mapId, "1f", fingerprint: "fp1", updatedAt: key.UpdatedAt, generation: "gen1");
        Assert.True(registry.TryPublishFloor(key, floor));
        Assert.Equal(Vpsg3IndexStatus.Ready, registry.GetStatus(mapId, "1f"));
        Assert.True(registry.TryGet(mapId, "1f", out var lease));
        lease?.Dispose();

        // 6. Invalidation transitions to Stale
        registry.InvalidateMaps(new HashSet<Guid> { mapId });
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(mapId, "1f"));
        Assert.False(registry.TryGet(mapId, "1f", out _));
    }

    [Fact]
    public void StaleBackgroundBuild_RejectedAndDisposed_WhenSlotSuperseded()
    {
        // Deterministic race test: K1 Building -> K2 Building -> K2 Publish -> K1 Late Publish
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var k1 = new Vpsg3IndexCacheKey(mapId, "1f", "fp_old", DateTimeOffset.UtcNow, "gen_old");
        var k2 = new Vpsg3IndexCacheKey(mapId, "1f", "fp_new", DateTimeOffset.UtcNow.AddMinutes(1), "gen_new");

        // K1 begins build
        Assert.True(registry.TryBeginBuild(k1));
        Assert.Equal(Vpsg3IndexStatus.Building, registry.GetStatus(mapId, "1f"));

        // Newer invalidation/update triggers K2 build
        Assert.True(registry.TryBeginBuild(k2));
        Assert.Equal(Vpsg3IndexStatus.Building, registry.GetStatus(mapId, "1f"));

        // K2 finishes and publishes
        var f2 = CreateDummyFloor(mapId, "1f", fingerprint: "fp_new", updatedAt: k2.UpdatedAt, generation: "gen_new");
        Assert.True(registry.TryPublishFloor(k2, f2));
        Assert.Equal(Vpsg3IndexStatus.Ready, registry.GetStatus(mapId, "1f", k2));

        // K1 late publish arrives -> must be rejected, and f1 disposed!
        var f1 = CreateDummyFloor(mapId, "1f", fingerprint: "fp_old", updatedAt: k1.UpdatedAt, generation: "gen_old");
        Assert.False(registry.TryPublishFloor(k1, f1));
        Assert.True(f1.IsDisposed);

        // K2 slot remains ready and untouched
        Assert.Equal(Vpsg3IndexStatus.Ready, registry.GetStatus(mapId, "1f", k2));
        Assert.True(registry.TryGet(k2, out var lease));
        using (lease)
        {
            Assert.Equal("fp_new", lease.Floor.CacheKey.ContentFingerprint);
        }
    }

    [Fact]
    public void StaleBuildFailure_Rejected_WhenSlotSuperseded()
    {
        // Deterministic race test: K1 Building -> K2 Building -> K1 Late Failure
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var k1 = new Vpsg3IndexCacheKey(mapId, "1f", "fp_old", DateTimeOffset.UtcNow, "gen_old");
        var k2 = new Vpsg3IndexCacheKey(mapId, "1f", "fp_new", DateTimeOffset.UtcNow.AddMinutes(1), "gen_new");

        Assert.True(registry.TryBeginBuild(k1));
        Assert.True(registry.TryBeginBuild(k2));

        // Late failure for K1 must be rejected and must NOT mark K2 as failed
        Assert.False(registry.RecordBuildFailure(k1, "Old failure"));
        Assert.Equal(Vpsg3IndexStatus.Building, registry.GetStatus(mapId, "1f", k2));
    }

    [Fact]
    public void InvalidateMaps_CorrectlyTransitionsBuildingAndFailedSlotsToStale()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var map1 = Guid.NewGuid();
        var map2 = Guid.NewGuid();

        var kBuilding = new Vpsg3IndexCacheKey(map1, "1f", "fp1", DateTimeOffset.UtcNow, "gen1");
        var kFailed = new Vpsg3IndexCacheKey(map1, "2f", "fp1", DateTimeOffset.UtcNow, "gen1");
        var kOther = new Vpsg3IndexCacheKey(map2, "1f", "fp2", DateTimeOffset.UtcNow, "gen2");

        Assert.True(registry.TryBeginBuild(kBuilding));
        Assert.True(registry.TryBeginBuild(kFailed));
        Assert.True(registry.RecordBuildFailure(kFailed, "Simulated failure"));
        Assert.True(registry.TryBeginBuild(kOther));

        // Even though Building and Failed slots have Floor == null, InvalidateMaps must transition them to Stale!
        registry.InvalidateMaps(new HashSet<Guid> { map1 });

        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(map1, "1f"));
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(map1, "2f"));
        Assert.Equal(Vpsg3IndexStatus.Building, registry.GetStatus(map2, "1f"));
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
        PublishToRegistry(registry, floor);

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

        PublishToRegistry(registry, CreateDummyFloor(map1, "1f"));
        PublishToRegistry(registry, CreateDummyFloor(map1, "2f"));
        PublishToRegistry(registry, CreateDummyFloor(map2, "1f"));

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
        PublishToRegistry(registry, originalFloor);

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
        PublishToRegistry(registry, floor);

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
            PublishToRegistry(registry, CreateDummyFloor(id, "1f"));
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
                var floor = CreateDummyFloor(id, "1f");
                if (registry.TryBeginBuild(floor.CacheKey))
                {
                    registry.TryPublishFloor(floor.CacheKey, floor);
                }
                else
                {
                    floor.Dispose();
                }
                Thread.Sleep(5);
            }
        })).ToArray();

        await Task.WhenAll([.. readTasks, .. writeTasks]);
    }
}
