using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class Vpsg3ApplicationLifecycleTests
{
    private static Vpsg3PreparedFloor CreateDummyFloor(Guid mapId, string floorKey = "1f")
    {
        var key = new Vpsg3IndexCacheKey(mapId, floorKey, "fp1", DateTimeOffset.UtcNow, "gen1");
        var wordsPerRow = (800 + 63) / 64;
        var bitset = new ulong[600 * wordsPerRow];
        var scalePrior = new Vpsg3ScalePrior(1.0d, 2.5d, true, string.Empty, 45d, 2.5d);
        return new Vpsg3PreparedFloor(key, 800, 600, 1200, scalePrior, wordsPerRow, bitset, 40000);
    }

    [Fact]
    public void MapCvRecognitionService_HoldsApplicationLifetimeRegistry_AndDisposesCleanly()
    {
        var repo = new MapRepository();
        var service = new MapCvRecognitionService(repo);

        Assert.NotNull(service.Vpsg3Registry);
        Assert.Equal(0, service.Vpsg3Registry.Count);

        // Register dummy floor into service registry
        var mapId = Guid.NewGuid();
        service.Vpsg3Registry.PublishFloor(CreateDummyFloor(mapId, "1f"));
        Assert.Equal(1, service.Vpsg3Registry.ReadyCount);

        // Dispose service
        service.Dispose();

        // Registry should be disposed; TryGet returns false
        Assert.False(service.Vpsg3Registry.TryGet(mapId, "1f", out _));
    }

    [Fact]
    public void RefreshCache_SynchronouslyInvalidatesChangedMaps_AndPreservesUnchanged()
    {
        var repo = new MapRepository();
        using var service = new MapCvRecognitionService(repo);

        var map1 = Guid.NewGuid();
        var map2 = Guid.NewGuid();

        service.Vpsg3Registry.PublishFloor(CreateDummyFloor(map1, "1f"));
        service.Vpsg3Registry.PublishFloor(CreateDummyFloor(map2, "1f"));
        Assert.Equal(2, service.Vpsg3Registry.ReadyCount);

        // Synchronous invalidation of map1
        service.InvalidateAndTriggerVpsg3Rebuild(
            maps: [],
            changedMapIds: new HashSet<Guid> { map1 });

        // map1 is immediately Stale and cannot be leased
        Assert.False(service.Vpsg3Registry.TryGet(map1, "1f", out _));
        Assert.Equal(Vpsg3IndexStatus.Stale, service.Vpsg3Registry.GetStatus(map1, "1f"));

        // map2 remains Ready and leasable
        Assert.True(service.Vpsg3Registry.TryGet(map2, "1f", out var lease2));
        using (lease2)
        {
            Assert.Equal(map2, lease2.Floor.CacheKey.MapId);
        }
    }

    [Theory]
    [InlineData(Vpsg3IndexStatus.Missing)]
    [InlineData(Vpsg3IndexStatus.Building)]
    [InlineData(Vpsg3IndexStatus.Failed)]
    [InlineData(Vpsg3IndexStatus.Stale)]
    public void Alignment_NeverBlocksOnNonReadyStates_ReturnsFalseImmediately(Vpsg3IndexStatus nonReadyStatus)
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var key = new Vpsg3IndexCacheKey(mapId, "1f", "fp1", DateTimeOffset.UtcNow, "gen1");

        switch (nonReadyStatus)
        {
            case Vpsg3IndexStatus.Missing:
                // No slot registered
                break;

            case Vpsg3IndexStatus.Building:
                registry.TryBeginBuild(key);
                break;

            case Vpsg3IndexStatus.Failed:
                registry.RecordBuildFailure(key, "Simulated build error");
                break;

            case Vpsg3IndexStatus.Stale:
                registry.PublishFloor(CreateDummyFloor(mapId, "1f"));
                registry.InvalidateMaps(new HashSet<Guid> { mapId });
                break;
        }

        // Non-blocking query: strictly returns false immediately (caller falls back to VPSG2)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = registry.TryGet(mapId, "1f", out var lease);
        sw.Stop();

        Assert.False(result);
        Assert.Null(lease);
        // Instant non-blocking execution (sub-millisecond)
        Assert.True(sw.ElapsedMilliseconds < 50);
    }
}
