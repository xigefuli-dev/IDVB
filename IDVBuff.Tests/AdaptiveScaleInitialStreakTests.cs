using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleInitialStreakTests
{
    [Fact]
    public async Task FifthHighQualityInitialResultIsImmediatelyReliable()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-streak-");
        try
        {
            var store = Store(directory);
            var coordinator = Coordinator(store);
            using var frame = Frame();
            var map = Map();
            AdaptiveAlignmentDecision? decision = null;

            for (var open = 1; open <= 5; open++)
            {
                decision = coordinator.EvaluateInitial(
                    Recognition(map, "1f", 1.0),
                    frame,
                    MapFeatureCacheSource.CrossResolutionValidated,
                    Evidence(open),
                    open);
                if (open < 5)
                    Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
                coordinator.EndOpen(open, "test");
            }

            Assert.NotNull(decision);
            Assert.Equal(AdaptiveScaleReliability.Reliable, decision!.Reliability);
            Assert.Equal(5, decision.ConsecutiveHighQualityCount);
            Assert.Equal(AdaptiveScaleReliabilityReason.InitialFiveStreak,
                decision.ReliabilityReason);
            await coordinator.DrainAsync();
            Assert.True(AdaptiveScaleStore.IsTrusted(StoreEntry(store, map, frame, "1f")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SameOpenAndKeyCannotVoteTwice()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();

        var first = coordinator.EvaluateInitial(
            Recognition(map, "1f", 1.0), frame, null, Evidence(1), 7);
        var duplicate = coordinator.EvaluateInitial(
            Recognition(map, "1f", 1.0), frame, null, Evidence(2), 7);

        Assert.Equal(1, first.ConsecutiveHighQualityCount);
        Assert.Equal(1, duplicate.ConsecutiveHighQualityCount);
    }

    [Fact]
    public void CachedFixedScaleNeitherVotesNorClearsIndependentSearchEvidence()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        Vote(coordinator, frame, map, 1, 1.0);
        var second = Vote(coordinator, frame, map, 2, 1.001);

        var cachedFixed = coordinator.EvaluateInitial(
            Recognition(map, "1f", 1.001),
            frame,
            MapFeatureCacheSource.Recovery,
            new AdaptiveScaleInitialEvidence(
                3,
                0.04,
                StructureValidated: true,
                ScaleIndependentlyEstimated: false),
            openId: 3);

        Assert.Equal(2, second.ConsecutiveHighQualityCount);
        Assert.Equal(2, cachedFixed.ConsecutiveHighQualityCount);
        Assert.Equal(AdaptiveScaleReliability.Provisional, cachedFixed.Reliability);
    }

    [Fact]
    public void LowQualityResultResetsAndScaleOutsideClusterRebuilds()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        Vote(coordinator, frame, map, 1, 1.0);
        Vote(coordinator, frame, map, 2, 1.0019);

        var rebuilt = Vote(coordinator, frame, map, 3, 1.005);
        Assert.Equal(1, rebuilt.ConsecutiveHighQualityCount);

        var reset = Vote(
            coordinator,
            frame,
            map,
            4,
            1.005,
            confidence: new AdaptiveScaleOptions().ReliableConfidence - 0.001);
        Assert.Equal(0, reset.ConsecutiveHighQualityCount);
        Assert.Equal(AdaptiveScaleReliability.Provisional, reset.Reliability);
    }

    [Fact]
    public void FiveQuantizedLowStructureResultsLockAtMedian()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        var scales = new[]
        {
            0.641543769d,
            0.644725177d,
            0.641543769d,
            0.644725177d,
            0.641543769d
        };
        AdaptiveAlignmentDecision? result = null;
        for (var index = 0; index < scales.Length; index++)
        {
            result = coordinator.EvaluateInitial(
                Recognition(map, "b1f", scales[index]),
                frame,
                null,
                new AdaptiveScaleInitialEvidence(
                    index + 1,
                    0.04d,
                    StructureValidated: true,
                    ScaleClusterTolerance: 0.006d,
                    ScaleResolutionRatio: 0.00493d),
                index + 1);
        }

        Assert.Equal(AdaptiveScaleReliability.Reliable, result!.Reliability);
        Assert.Equal(5, result.ConsecutiveHighQualityCount);
        Assert.Equal(0.641543769d, result.RecognitionToRender.Result.OverlayTransform!.ScaleX, 8);
    }

    [Fact]
    public void WrongLowStructureBasinRebuildsEvidence()
    {
        var options = new AdaptiveScaleOptions();
        var key = AdaptiveScaleKey.Create(
            Map(),
            "b1f",
            new MapScreenRect(0, 0, 1920, 1080),
            new MapScreenRect(303, 25, 1314, 1055));
        var streak = new AdaptiveScaleInitialStreakState(key, options);
        streak.Observe(1, 0.57d, 0.9d, true, DateTimeOffset.UtcNow, clusterTolerance: 0.006d);

        var rebuilt = streak.Observe(
            2,
            1.36d,
            0.9d,
            true,
            DateTimeOffset.UtcNow,
            clusterTolerance: 0.006d);

        Assert.True(rebuilt.Rebuilt);
        Assert.Single(rebuilt.Snapshot.Samples);
        Assert.Equal(1.36d, rebuilt.Snapshot.MedianScale, 8);
    }

    [Fact]
    public void FloorsUseIndependentStreaks()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        for (var open = 1; open <= 5; open++)
            Vote(coordinator, frame, map, open, 1.0, floor: "1f");
        var secondFloor = Vote(coordinator, frame, map, 6, 1.2, floor: "2f");

        Assert.Equal(1, secondFloor.ConsecutiveHighQualityCount);
        Assert.Equal(AdaptiveScaleReliability.Provisional, secondFloor.Reliability);
    }

    [Fact]
    public void LatestMap18LogSequenceMakesFirstFloorReliableOnly()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        var firstFloorConfidence = new[]
        {
            0.8956951064537627,
            0.8955473117408419,
            0.9043777997564546,
            0.9025458203142722,
            0.9058777438577305
        };
        AdaptiveAlignmentDecision? firstFloor = null;
        for (var index = 0; index < firstFloorConfidence.Length; index++)
        {
            firstFloor = Vote(
                coordinator,
                frame,
                map,
                index + 1,
                1.1907072012263284,
                firstFloorConfidence[index],
                "1f");
        }

        Vote(coordinator, frame, map, 20, 1.135201257655576, 0.8069983261295984, "2f");
        Vote(coordinator, frame, map, 21, 1.135201257655576, 0.8271435980856471, "2f");
        var secondFloor = Vote(
            coordinator, frame, map, 22, 1.135201257655576, 0.8321697804086229, "2f");

        Assert.Equal(AdaptiveScaleReliability.Reliable, firstFloor!.Reliability);
        Assert.Equal(5, firstFloor.ConsecutiveHighQualityCount);
        Assert.Equal(AdaptiveScaleReliability.Provisional, secondFloor.Reliability);
        Assert.Equal(3, secondFloor.ConsecutiveHighQualityCount);
    }

    [Theory]
    [InlineData(MapAlignmentEvidenceKind.Structure, false, false, true, 0.10, 0.90, 1)]
    [InlineData(MapAlignmentEvidenceKind.Structure, true, false, true, 0.10, 0.90, 0)]
    [InlineData(MapAlignmentEvidenceKind.Structure, false, true, true, 0.10, 0.90, 0)]
    [InlineData(MapAlignmentEvidenceKind.DualGate, false, false, true, 0.10, 0.90, 0)]
    [InlineData(MapAlignmentEvidenceKind.Structure, false, false, false, 0.10, 0.90, 0)]
    [InlineData(MapAlignmentEvidenceKind.Structure, false, false, true, 0.03, 0.90, 0)]
    public void InitialQualificationUsesExistingStructureRules(
        MapAlignmentEvidenceKind kind,
        bool reused,
        bool skipped,
        bool validated,
        double margin,
        double confidence,
        int expected)
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var recognition = Recognition(Map(), "1f", 1.0, confidence, kind, reused, skipped, margin);

        var decision = coordinator.EvaluateInitial(
            recognition,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.04, validated),
            1);

        Assert.Equal(expected, decision.ConsecutiveHighQualityCount);
    }

    [Fact]
    public void ProvisionalDoesNotRequestWideSearch()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        Vote(coordinator, frame, map, 1, 1.0);
        var key = AdaptiveScaleKey.Create(map, "1f", frame.ClientBounds, frame.ViewportBounds);

        Assert.False(coordinator.RequiresWideScaleSearch(key, 1));
    }

    [Fact]
    public void LowQualityInitialResultImmediatelyRequestsWideRecovery()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        var decision = Vote(
            coordinator,
            frame,
            map,
            1,
            1.08584,
            new AdaptiveScaleOptions().ReliableConfidence - 0.01);
        var key = AdaptiveScaleKey.Create(map, "1f", frame.ClientBounds, frame.ViewportBounds);

        Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
        Assert.True(coordinator.RequiresWideScaleSearch(key, 1));
        Assert.False(coordinator.IsQualifiedInitialResult(
            decision.RecognitionToRender,
            0.04));
    }

    [Fact]
    public void FifthResultLocksFloorScaleAgainstDirectOrbChanges()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        for (var open = 1; open <= 5; open++)
        {
            Vote(coordinator, frame, map, open, 1.0);
            if (open < 5)
                coordinator.EndOpen(open, "next-open");
        }
        var key = AdaptiveScaleKey.Create(map, "1f", frame.ClientBounds, frame.ViewportBounds);

        var orb = coordinator.EvaluateOrbObservation(
            key,
            5,
            Recognition(map, "1f", 1.04).Result.OverlayTransform!,
            1.04,
            DateTimeOffset.UtcNow);

        Assert.Equal(1.0, orb.Transform.ScaleX, 8);
        Assert.True(orb.RequestStructureProbe);
        Assert.Equal(AdaptiveScaleState.Challenged, orb.State);
    }

    [Fact]
    public async Task FifthResultDoesNotWaitForOrDependOnDiskWrite()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-streak-");
        try
        {
            var occupied = Directory.CreateDirectory(
                Path.Combine(directory.FullName, "adaptive-scale-cache.json"));
            var store = new AdaptiveScaleStore(occupied.FullName);
            var coordinator = Coordinator(store);
            using var frame = Frame();
            var map = Map();
            AdaptiveAlignmentDecision? fifth = null;
            for (var open = 1; open <= 5; open++)
                fifth = Vote(coordinator, frame, map, open, 1.0);

            Assert.Equal(AdaptiveScaleReliability.Reliable, fifth!.Reliability);
            Assert.True(fifth.AllowReliableSession);
            await coordinator.DrainAsync();
            Assert.Null(StoreEntry(store, map, frame, "1f"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ManualRuntimeLockMakesOnlyCurrentFloorReliableWithoutWritingStore()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-manual-lock-");
        try
        {
            var path = Path.Combine(directory.FullName, "adaptive-scale-cache.json");
            var store = new AdaptiveScaleStore(path);
            var coordinator = Coordinator(store);
            using var frame = Frame();
            var map = Map();
            var firstFloor = Vote(coordinator, frame, map, 1, 1.0, floor: "1f");
            await coordinator.DrainAsync();
            var persistedBeforeLock = await File.ReadAllBytesAsync(path);
            var firstFloorKey = AdaptiveScaleKey.Create(
                map, "1f", frame.ClientBounds, frame.ViewportBounds);

            Assert.True(coordinator.TryLockCurrentScale(firstFloorKey, 1, 1.0));
            Assert.True(coordinator.IsConfirmedTransform(
                firstFloorKey,
                1,
                firstFloor.RecognitionToRender.Result.OverlayTransform!));
            await coordinator.DrainAsync();
            Assert.Equal(persistedBeforeLock, await File.ReadAllBytesAsync(path));

            coordinator.EndOpen(1, "switch-floor");
            var secondFloor = Vote(coordinator, frame, map, 2, 1.2, floor: "2f");
            var secondFloorKey = AdaptiveScaleKey.Create(
                map, "2f", frame.ClientBounds, frame.ViewportBounds);
            Assert.False(coordinator.IsConfirmedTransform(
                secondFloorKey,
                2,
                secondFloor.RecognitionToRender.Result.OverlayTransform!));
            await coordinator.DrainAsync();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.72, true)]
    [InlineData(0.81, true)]
    [InlineData(0.64, false)]
    [InlineData(0.50, false)]
    public void DefaultGateUsesTunedConfidenceFloor(
        double confidence,
        bool usable)
    {
        var coordinator = Coordinator();
        var recognition = Recognition(Map(), "1f", 1.0, confidence);

        // 当前调教后的正常门槛为 0.65；侧门种子和缓存命中不再
        // 被过高的旧门槛假设挡住。
        Assert.Equal(usable, coordinator.IsUsableInitialResult(
            recognition, 0.04));
        Assert.Equal(usable, coordinator.IsQualifiedInitialResult(
            recognition, 0.04));
    }

    [Fact]
    public void RelaxedGateStillRequiresValidatedStructureAndMargin()
    {
        var coordinator = Coordinator();
        var map = Map();

        // 结构未验证 / 跳过验证 / 复用上次变换 / 候选 margin 不足 → 均不可用
        Assert.False(coordinator.IsUsableInitialResult(
            Recognition(map, "1f", 1.0, 0.90), 0.04, structureValidated: false));
        Assert.False(coordinator.IsUsableInitialResult(
            Recognition(map, "1f", 1.0, 0.90, skipped: true), 0.04));
        Assert.False(coordinator.IsUsableInitialResult(
            Recognition(map, "1f", 1.0, 0.90, reused: true), 0.04));
        Assert.False(coordinator.IsUsableInitialResult(
            Recognition(map, "1f", 1.0, 0.90, margin: 0.02), 0.04));
        // 完全合格 → 可用
        Assert.True(coordinator.IsUsableInitialResult(
            Recognition(map, "1f", 1.0, 0.90), 0.04));
    }

    private static AdaptiveAlignmentDecision Vote(
        AdaptiveScaleCoordinator coordinator,
        CapturedGameFrame frame,
        MapRecord map,
        long open,
        double scale,
        double confidence = 0.90,
        string floor = "1f") =>
        coordinator.EvaluateInitial(
            Recognition(map, floor, scale, confidence),
            frame,
            null,
            Evidence(open),
            open);

    private static AdaptiveScaleStoreEntry? StoreEntry(
        AdaptiveScaleStore store,
        MapRecord map,
        CapturedGameFrame frame,
        string floor) =>
        store.TryGet(AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds));

    private static AdaptiveScaleCoordinator Coordinator(AdaptiveScaleStore? store = null) =>
        new(new AdaptiveScaleOptions(), store);

    private static AdaptiveScaleStore Store(DirectoryInfo directory) =>
        new(Path.Combine(directory.FullName, "adaptive-scale-cache.json"));

    private static AdaptiveScaleInitialEvidence Evidence(long frameId) =>
        new(frameId, 0.04, StructureValidated: true);

    private static CapturedGameFrame Frame() => new(
        new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
        new MapScreenRect(0, 0, 1920, 1080),
        new MapScreenRect(303, 25, 1314, 1055),
        IntPtr.Zero);

    private static MapRecord Map() => new()
    {
        Id = Guid.NewGuid(),
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Title = "test"
    };

    private static RuntimeMapRecognition Recognition(
        MapRecord map,
        string floor,
        double scale,
        double confidence = 0.90,
        MapAlignmentEvidenceKind kind = MapAlignmentEvidenceKind.Structure,
        bool reused = false,
        bool skipped = false,
        double margin = 0.10) => new()
    {
        Map = map,
        Result = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = floor,
            Confidence = confidence,
            LocalizationConfidence = confidence,
            IdentityConfidence = 0.90,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            StructureCandidateMargin = margin,
            StructureRejectionReason = MapStructureRejectionReason.None,
            EvidenceKind = kind,
            ReusedLastTransform = reused,
            SkippedStructureValidation = skipped
        }
    };
}
