using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleSpatialValidationTests
{
    [Fact]
    public async Task SmallRoomLowSpanPreventsInitialFiveStreakLocking()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-spatial-smallroom-");
        try
        {
            var store = Store(directory);
            var coordinator = Coordinator(store);
            using var frame = Frame();
            var map = Map();
            AdaptiveAlignmentDecision? decision = null;

            // 模拟小房间：连续 5 次高质量对齐，但 SpatialSpanRatio 仅 0.20（低于门槛 0.35）
            for (var open = 1; open <= 5; open++)
            {
                decision = coordinator.EvaluateInitial(
                    Recognition(map, "1f", 1.0),
                    frame,
                    MapFeatureCacheSource.CrossResolutionValidated,
                    Evidence(open, span: 0.20d, edgePixels: 100, cx: 500, cy: 500),
                    open);

                Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
                coordinator.EndOpen(open, "test");
            }

            Assert.NotNull(decision);
            // 即使连开 5 次，由于未跨过空间几何跨度门槛，绝不允许草率锁定
            Assert.Equal(AdaptiveScaleReliability.Provisional, decision!.Reliability);
            Assert.Equal(0, decision.ConsecutiveHighQualityCount);

            await coordinator.DrainAsync();
            Assert.Null(StoreEntry(store, map, frame, "1f"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void StationaryReopeningWithoutMovementDiversityPreventsPrematureLocking()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        AdaptiveAlignmentDecision? decision = null;

        // 连续 5 次开图，跨度为 0.40（中等跨度），但全部在原地 (500, 500)
        for (var open = 1; open <= 5; open++)
        {
            decision = coordinator.EvaluateInitial(
                Recognition(map, "1f", 1.0),
                frame,
                MapFeatureCacheSource.CrossResolutionValidated,
                Evidence(open, span: 0.40d, edgePixels: 100, cx: 500, cy: 500),
                open);
            coordinator.EndOpen(open, "test");
        }

        Assert.NotNull(decision);
        // 原地连开 5 次缺乏位移多样性，禁止草率判定收敛
        Assert.Equal(AdaptiveScaleReliability.Provisional, decision!.Reliability);

        // 第 6 次玩家跑图产生了位移 (530, 530)，距离 42.4px > 15px
        decision = coordinator.EvaluateInitial(
            Recognition(map, "1f", 1.0),
            frame,
            MapFeatureCacheSource.CrossResolutionValidated,
            Evidence(6, span: 0.40d, edgePixels: 100, cx: 530, cy: 530),
            openId: 6);

        Assert.Equal(AdaptiveScaleReliability.Reliable, decision.Reliability);
        Assert.Equal(AdaptiveScaleReliabilityReason.InitialFiveStreak, decision.ReliabilityReason);
    }

    [Fact]
    public void ScaleRefinementOnSpanExpansionUpdatesRuntimeScale()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();

        // 步骤 1: 在中等跨度 0.40 下满足多样性锁定为 1.00
        for (var open = 1; open <= 5; open++)
        {
            var cx = 500.0 + (open * 5.0); // 产生 25px 跨度
            coordinator.EvaluateInitial(
                Recognition(map, "1f", 1.0),
                frame,
                MapFeatureCacheSource.CrossResolutionValidated,
                Evidence(open, span: 0.40d, edgePixels: 100, cx: cx, cy: 500),
                open);
            coordinator.EndOpen(open, "test");
        }

        // 验证已锁定
        var lockedDecision = coordinator.EvaluateInitial(
            Recognition(map, "1f", 1.0),
            frame,
            null,
            Evidence(6, span: 0.40d, edgePixels: 100, cx: 525, cy: 500),
            6);
        Assert.Equal(AdaptiveScaleReliability.Reliable, lockedDecision.Reliability);

        // 步骤 2: 探索新大区域，跨度显著扩大到 0.68（扩大了 0.28 > 0.20），且微调 scale 1.015
        var refinedDecision = coordinator.EvaluateInitial(
            Recognition(map, "1f", 1.015),
            frame,
            null,
            Evidence(7, span: 0.68d, edgePixels: 120, cx: 700, cy: 600),
            7);

        // 应当精修成功并在渲染中输出演进尺度 1.015
        Assert.Equal(1.015, refinedDecision.RecognitionToRender.Result.OverlayTransform!.ScaleX, 4);
    }

    [Fact]
    public void AdaptiveScaleOptionsDefaultsAndNormalizationAreConsistent()
    {
        var options = new AdaptiveScaleOptions();
        Assert.Equal(0.35d, options.MinimumSpatialSpanRatio);
        Assert.Equal(60, options.MinimumLockEdgePixels);
        Assert.Equal(15.0d, options.MinimumMovementDiversityPixels);
        Assert.Equal(0.20d, options.SpanExpansionRefinementThreshold);
        Assert.Equal(2.2d, options.ChamferProactiveRecoveryThreshold);

        options.MinimumSpatialSpanRatio = 0.01d;
        options.ChamferProactiveRecoveryThreshold = 5.0d;
        options.Normalize();

        Assert.True(options.MinimumSpatialSpanRatio >= 0.10d);
        Assert.True(options.ChamferProactiveRecoveryThreshold <= 2.9d);
    }

    private static AdaptiveScaleCoordinator Coordinator(AdaptiveScaleStore? store = null) =>
        new(
            new AdaptiveScaleOptions { MinimumObservationSpacingMilliseconds = 0 },
            store);

    private static AdaptiveScaleStore Store(DirectoryInfo directory) =>
        new(Path.Combine(directory.FullName, "adaptive-scale-cache.json"));

    private static AdaptiveScaleInitialEvidence Evidence(
        long frameId,
        double span = 0.50d,
        int edgePixels = 100,
        double cx = 500,
        double cy = 500) =>
        new(
            frameId,
            0.04,
            StructureValidated: true,
            SpatialSpanRatio: span,
            QueryEdgePixels: edgePixels,
            CenterX: cx,
            CenterY: cy);

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
        double scale) => new()
    {
        Map = map,
        Result = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = floor,
            Confidence = 0.90,
            LocalizationConfidence = 0.90,
            IdentityConfidence = 0.90,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            StructureCandidateMargin = 0.10,
            StructureRejectionReason = MapStructureRejectionReason.None,
            EvidenceKind = MapAlignmentEvidenceKind.Structure
        }
    };

    private static AdaptiveScaleStoreEntry? StoreEntry(
        AdaptiveScaleStore store,
        MapRecord map,
        CapturedGameFrame frame,
        string floor) =>
        store.TryGet(AdaptiveScaleKey.Create(
            map,
            floor,
            frame.ClientBounds,
            frame.ViewportBounds));
}
