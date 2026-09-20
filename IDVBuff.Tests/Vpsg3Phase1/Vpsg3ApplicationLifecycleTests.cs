using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase1;

public sealed class Vpsg3ApplicationLifecycleTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper? _output;

    public Vpsg3ApplicationLifecycleTests(Xunit.Abstractions.ITestOutputHelper? output = null)
    {
        _output = output;
    }

    private static Vpsg3PreparedFloor CreateDummyFloor(Guid mapId, string floorKey = "1f")
    {
        var key = new Vpsg3IndexCacheKey(mapId, floorKey, "fp1", DateTimeOffset.UtcNow, "gen1");
        var wordsPerRow = (800 + 63) / 64;
        var bitset = new ulong[600 * wordsPerRow];
        var scalePrior = new Vpsg3ScalePrior(1.0d, 2.5d, true, string.Empty, 45d, 2.5d);
        return new Vpsg3PreparedFloor(key, 800, 600, 1200, scalePrior, wordsPerRow, bitset, 40000);
    }

    private static void PublishToRegistry(IVpsg3PreparedIndexRegistry registry, Vpsg3PreparedFloor floor)
    {
        Assert.True(registry.TryBeginBuild(floor.CacheKey));
        Assert.True(registry.TryPublishFloor(floor.CacheKey, floor));
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
        PublishToRegistry(service.Vpsg3Registry, CreateDummyFloor(mapId, "1f"));
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

        PublishToRegistry(service.Vpsg3Registry, CreateDummyFloor(map1, "1f"));
        PublishToRegistry(service.Vpsg3Registry, CreateDummyFloor(map2, "1f"));
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

    [Fact]
    public void PrebuiltGenerationIdentity_WhenLineOrAlgorithmChanges_InvalidatesCacheKey()
    {
        using var registry = new Vpsg3PreparedIndexRegistry();
        var mapId = Guid.NewGuid();
        var assetV1 = new PrebuiltStructureLineAsset
        {
            FileName = "prebuilt-1f.png",
            Sha256 = "1111111111111111111111111111111111111111111111111111111111111111",
            SourceSha256 = "2222222222222222222222222222222222222222222222222222222222222222",
            AlgorithmId = "algo1",
            AlgorithmFileName = "algo.idva",
            AlgorithmSha256 = "3333333333333333333333333333333333333333333333333333333333333333",
            AlgorithmSchemaVersion = "1.0",
            Width = 800,
            Height = 600,
            FileLength = 1234
        };

        var genV1 = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(assetV1, 1);
        var now = DateTimeOffset.UtcNow;
        var keyV1 = new Vpsg3IndexCacheKey(mapId, "1f", "fp1", now, genV1, 1);

        var floor = new Vpsg3PreparedFloor(
            keyV1, 800, 600, 1000,
            new Vpsg3ScalePrior(1.0, 3.0, true, string.Empty, 40, 3.0),
            (800 + 63) / 64,
            new ulong[600 * ((800 + 63) / 64)],
            30000);

        PublishToRegistry(registry, floor);
        Assert.True(registry.TryGet(keyV1, out var lease1));
        lease1?.Dispose();

        // 1. Line SHA256 changes -> generation identity changes
        var assetV2LineChange = assetV1.Clone();
        assetV2LineChange.Sha256 = "4444444444444444444444444444444444444444444444444444444444444444";
        var genV2Line = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(assetV2LineChange, 1);
        var keyV2Line = new Vpsg3IndexCacheKey(mapId, "1f", "fp1", now, genV2Line, 1);

        Assert.False(registry.TryGet(keyV2Line, out _));
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(mapId, "1f", keyV2Line));

        // 2. Algorithm SHA256 changes -> generation identity changes
        var assetV3AlgoChange = assetV1.Clone();
        assetV3AlgoChange.AlgorithmSha256 = "5555555555555555555555555555555555555555555555555555555555555555";
        var genV3Algo = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(assetV3AlgoChange, 1);
        var keyV3Algo = new Vpsg3IndexCacheKey(mapId, "1f", "fp1", now, genV3Algo, 1);

        Assert.False(registry.TryGet(keyV3Algo, out _));
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(mapId, "1f", keyV3Algo));

        // 3. Algorithm Schema Version changes -> generation identity changes
        var assetV4VerChange = assetV1.Clone();
        assetV4VerChange.AlgorithmSchemaVersion = "2.0";
        var genV4Ver = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(assetV4VerChange, 1);
        var keyV4Ver = new Vpsg3IndexCacheKey(mapId, "1f", "fp1", now, genV4Ver, 1);

        Assert.False(registry.TryGet(keyV4Ver, out _));
        Assert.Equal(Vpsg3IndexStatus.Stale, registry.GetStatus(mapId, "1f", keyV4Ver));
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
                registry.TryBeginBuild(key);
                registry.RecordBuildFailure(key, "Simulated build error");
                break;

            case Vpsg3IndexStatus.Stale:
                PublishToRegistry(registry, CreateDummyFloor(mapId, "1f"));
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

    [Fact]
    public async Task Benchmark_RealMaps_FullPrebuiltIndexBuild()
    {
        var repo = new MapRepository();
        var maps = await repo.GetMapsAsync();
        if (maps.Count == 0)
            return;

        var totalFloors = maps.Sum(m => m.Floors.Count);
        var eligibleTasks = new List<(MapRecord Map, FloorDefinition Floor, string LinePath)>();

        foreach (var m in maps)
        {
            foreach (var f in m.Floors)
            {
                if (f.PrebuiltStructureLine?.IsComplete is true
                    && string.Equals(f.PrebuiltStructureLine.SourceSha256, f.RecognitionSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var path = repo.GetPrebuiltStructureLinePath(m, f.Key);
                        if (File.Exists(path))
                            eligibleTasks.Add((m, f, path));
                    }
                    catch
                    {
                    }
                }
            }
        }

        var eligibleFloors = eligibleTasks.Count;
        var ineligibleFloors = totalFloors - eligibleFloors;

        // Measure individual floor build latencies
        var perFloorTimes = new List<double>();
        foreach (var item in eligibleTasks)
        {
            using var image = OpenCvSharp.Cv2.ImRead(item.LinePath, OpenCvSharp.ImreadModes.Grayscale);
            var swSingle = System.Diagnostics.Stopwatch.StartNew();
            var gen = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(item.Floor.PrebuiltStructureLine!, 1);
            var key = new Vpsg3IndexCacheKey(item.Map.Id, item.Floor.Key, "test_fp", item.Map.UpdatedAt, gen);
            using var prepared = Vpsg3PreparedIndexBuilder.BuildFromMat(image, key);
            swSingle.Stop();
            perFloorTimes.Add(swSingle.Elapsed.TotalMilliseconds);
        }

        perFloorTimes.Sort();
        var p50 = perFloorTimes[(int)(perFloorTimes.Count * 0.50)];
        var p95 = perFloorTimes[(int)(perFloorTimes.Count * 0.95)];
        var max = perFloorTimes[^1];

        // Measure bounded parallel application rebuild
        using var service = new MapCvRecognitionService(repo);
        var changedMapIds = new HashSet<Guid>(maps.Select(m => m.Id));

        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        service.InvalidateAndTriggerVpsg3Rebuild(maps, changedMapIds);

        while (service.Vpsg3Registry.ReadyCount < eligibleFloors && swTotal.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
        }
        swTotal.Stop();

        var readyFloors = service.Vpsg3Registry.ReadyCount;
        var totalBytes = service.Vpsg3Registry.TotalMemoryBytes;
        var totalMb = totalBytes / (1024.0 * 1024.0);
        var avgKb = readyFloors > 0 ? (totalBytes / (double)readyFloors) / 1024.0 : 0d;

        _output?.WriteLine($"[TOTAL FLOORS]      : {totalFloors}");
        _output?.WriteLine($"[ELIGIBLE FLOORS]   : {eligibleFloors}");
        _output?.WriteLine($"[READY FLOORS]      : {readyFloors}");
        _output?.WriteLine($"[INELIGIBLE FLOORS] : {ineligibleFloors}");
        _output?.WriteLine($"[P50 BUILD LATENCY] : {p50:F2} ms");
        _output?.WriteLine($"[P95 BUILD LATENCY] : {p95:F2} ms");
        _output?.WriteLine($"[MAX BUILD LATENCY] : {max:F2} ms");
        _output?.WriteLine($"[TOTAL REBUILD TIME]: {swTotal.Elapsed.TotalMilliseconds:F2} ms (Parallelism=3)");
        _output?.WriteLine($"[TOTAL RESIDENT MEM]: {totalMb:F2} MB ({totalBytes:N0} bytes)");
        _output?.WriteLine($"[AVG MEM PER FLOOR] : {avgKb:F2} KB");

        Assert.Equal(eligibleFloors, readyFloors);
    }
}
