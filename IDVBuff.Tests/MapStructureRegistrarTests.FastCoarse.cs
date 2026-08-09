using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void ProcessCachedReference_NoPathDoesNotCacheAndReturnsDirectly()
    {
        // P2-1: When referencePath is null, no caching occurs and the
        // caller receives the result directly (no Clone needed).
        var preprocessor = new MapStructurePreprocessor();
        using var reference = BuildReference();

        var result = preprocessor.ProcessCachedReference(
            reference, null, out _, out var cacheHit);

        Assert.NotNull(result);
        Assert.False(cacheHit);
        // Clean up — the caller owns this instance.
        result.Dispose();
    }

    private static Mat BuildReference()
    {
        var image = new Mat(new Size(480, 360), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(35, 35, 90, 70), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(185, 25, 120, 55), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(350, 50, 65, 115), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(70, 175, 75, 120), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(210, 145, 95, 105), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(345, 235, 95, 75), Scalar.White, -1);
        Cv2.Line(image, new Point(125, 70), new Point(185, 52), Scalar.White, 18);
        Cv2.Line(image, new Point(275, 80), new Point(260, 145), Scalar.White, 16);
        Cv2.Line(image, new Point(145, 225), new Point(210, 200), Scalar.White, 14);
        Cv2.Line(image, new Point(305, 210), new Point(345, 270), Scalar.White, 12);
        Cv2.Circle(image, new Point(255, 200), 22, Scalar.Black, -1);
        return image;
    }

    private static void DrawRepeatedGroup(Mat image, int x)
    {
        Cv2.Rectangle(image, new Rect(x, 45, 70, 55), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(x + 105, 35, 65, 80), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(x + 35, 135, 95, 55), Scalar.White, -1);
        Cv2.Line(
            image,
            new Point(x + 65, 82),
            new Point(x + 115, 75),
            Scalar.White,
            14);
        Cv2.Line(
            image,
            new Point(x + 85, 110),
            new Point(x + 85, 145),
            Scalar.White,
            12);
        Cv2.Circle(image, new Point(x + 82, 163), 13, Scalar.Black, -1);
    }

    // ═══════════════════════════════════════════════════════════════
    // 快速粗搜索单元测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FastAlignment_DefaultEnabled()
    {
        var tuning = new MapStructureRegistrationTuning();
        tuning.Normalize();

        Assert.True(tuning.EnableFastAlignment);
        Assert.True(tuning.FastFallbackToLegacy);
        Assert.False(tuning.FastAlignmentShadowMode);
        Assert.Equal(2, tuning.FastCoarseDownsampleFactor);
        Assert.Equal(5, tuning.FastCoarseTopK);
    }

    [Fact]
    public void FastAlignment_TuningRoundTrips()
    {
        var original = new MapStructureRegistrationTuning
        {
            EnableFastAlignment = true,
            FastFallbackToLegacy = false,
            FastAlignmentShadowMode = true,
            FastCoarseDownsampleFactor = 8,
            FastCoarseTopK = 10,
            FastCoarseNmsRadius = 24,
            FastCoarseMaxDimension = 200
        };

        var clone = original.Clone();
        clone.Normalize();

        Assert.True(clone.EnableFastAlignment);
        Assert.False(clone.FastFallbackToLegacy);
        Assert.True(clone.FastAlignmentShadowMode);
        Assert.Equal(8, clone.FastCoarseDownsampleFactor);
        Assert.Equal(10, clone.FastCoarseTopK);
        Assert.Equal(24, clone.FastCoarseNmsRadius);
        Assert.Equal(200, clone.FastCoarseMaxDimension);
    }

    [Fact]
    public void FastAlignment_TuningClamped()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            FastCoarseDownsampleFactor = 1,  // below min
            FastCoarseTopK = 1,              // below min
            FastCoarseNmsRadius = 1,         // below min
            FastCoarseMaxDimension = 10      // below min
        };
        tuning.Normalize();

        Assert.Equal(2, tuning.FastCoarseDownsampleFactor);
        Assert.Equal(3, tuning.FastCoarseTopK);
        Assert.Equal(4, tuning.FastCoarseNmsRadius);
        Assert.Equal(40, tuning.FastCoarseMaxDimension);
    }

    [Fact]
    public void FastCoarseAlign_FindsCorrectTranslation_WithStructuredQuery()
    {
        // 使用与 DistinctExploredStructureRecoversTranslation 相同场景
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var tuning = TestFastTuning();
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: expectedOffsetX + 12d,
                offsetY: expectedOffsetY - 9d),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // 快速路径可能因粗搜索精度不足而回退到 Legacy，
        // 但无论哪种路径，结果都应该正确
        Assert.True(result.Accepted, result.FailureReason);
        Assert.InRange(
            Math.Abs(result.Transform!.OffsetX - expectedOffsetX),
            0d,
            4d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetY - expectedOffsetY),
            0d,
            4d);
        // 如果快速路径成功，应有候选计数
        if (result.UsedFastStrategy)
        {
            Assert.True(result.FastCoarseCandidateCount > 0);
        }
    }

    [Fact]
    public void FastCoarseAlign_CandidatesRankedByCompositeCost()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var tuning = TestFastTuning();
        // 增大候选数以增加快速路径成功率
        tuning.FastCoarseTopK = 10;
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Candidates.Count >= 1,
            $"Expected at least 1 candidate, got {result.Candidates.Count}");

        for (var i = 1; i < result.Candidates.Count; i++)
        {
            Assert.True(
                result.Candidates[i - 1].CompositeCost
                    <= result.Candidates[i].CompositeCost + 0.001d,
                $"Candidate[{i - 1}] cost {result.Candidates[i - 1].CompositeCost:F3} "
                + $"should be ≤ Candidate[{i}] cost {result.Candidates[i].CompositeCost:F3}");
        }
    }

    [Fact]
    public void FastCoarseAlign_FallbackToLegacy_WhenRejected()
    {
        // 使用一个非常小的 query 确保快速路径因结构不足而拒绝
        // FastFallbackToLegacy=true 时会回退到 Legacy
        using var reference = BuildReference();
        using var live = new Mat(
            new Size(30, 25),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30));  // 低对比度、少结构
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = true;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // 无论快速路径还是 Legacy 都不应该接受这种 query
        Assert.False(result.Accepted);
        // 回退模式：UsedFastStrategy 为 Legacy 的结果，因此应为 false
    }

    [Fact]
    public void FastCoarseAlign_NoFallback_ReturnsRejectionWithoutCrashing()
    {
        using var reference = BuildReference();
        // 使用极小的 query 确保因结构不足而被快速路径直接拒绝
        using var live = new Mat(
            new Size(20, 15),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30));
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = false;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // 无回退时直接返回快速路径的拒绝结果 — 不应崩溃
        Assert.False(result.Accepted);
        // 注意：用于输入结构不足的早期拒绝不会设置 UsedFastStrategy，
        // 因为它在候选收集之前就退出了
    }

    [Fact]
    public void ShadowMode_ReturnsLegacyResult()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.FastAlignmentShadowMode = true;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // Shadow Mode 下 Legacy 应该是最终的返回结果，
        // UsedFastStrategy 应为 false
        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy);
    }

    [Fact]
    public void ShadowMode_WithFastEnabled_StillReturnsLegacyNotFast()
    {
        // P0-3: When both FastAlignmentShadowMode and EnableFastAlignment
        // are true, the result MUST come from Legacy, not Fast.
        // This verifies Shadow takes priority over production Fast.
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;       // production Fast enabled
        tuning.FastFallbackToLegacy = false;     // production Fast would NOT fallback
        tuning.FastAlignmentShadowMode = true;   // but Shadow overrides

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // Even though EnableFastAlignment=true and FastFallbackToLegacy=false
        // (which would force a Fast-only return in production mode),
        // Shadow mode ensures Legacy is returned.
        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy,
            "Shadow mode must return Legacy (UsedFastStrategy=false), not Fast");
    }

    [Fact]
    public void ProductionFastNoFallback_ReturnsFastFailureWhenRejected()
    {
        // P0-3 matrix: EnableFastAlignment=true, FastFallbackToLegacy=false,
        // Shadow=false, TrackingMode=false. Fast fails → return Fast failure.
        using var reference = BuildReference();
        using var live = new Mat(
            new Size(20, 15),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30)); // tiny, low-structure
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = false;
        tuning.FastAlignmentShadowMode = false;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // Must return Fast's failure, not fall through to Legacy.
        Assert.False(result.Accepted);
    }

    [Fact]
    public void ProductionLegacyOnly_NoFastExecution()
    {
        // P0-3 matrix: EnableFastAlignment=false, Shadow=false.
        // Only Legacy should run.
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = false;
        tuning.FastAlignmentShadowMode = false;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy,
            "Pure Legacy mode should not set UsedFastStrategy");
    }

    [Fact]
    public void LegacySearch_ReportsSubstageTimings()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        var tuning = TestTuning();
        tuning.EnableFastAlignment = false;
        tuning.EnableVisibleAwareInjection = false;
        tuning.EnableVisibleAwareShadow = false;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy);
        Assert.True(result.SearchMilliseconds > 0d);
        Assert.True(result.DistanceMapMilliseconds >= 0d);
        Assert.True(result.QueryConstructionMilliseconds >= 0d);
        Assert.True(result.HistoryCandidateMilliseconds >= 0d);
        Assert.True(result.FeatureVotingMilliseconds >= 0d);
        Assert.True(result.PyramidSearchMilliseconds >= 0d);
        Assert.True(result.LocalTemplateSearchMilliseconds >= 0d);
        Assert.True(result.GlobalTemplateSearchMilliseconds >= 0d);
        Assert.True(result.CandidateRankingMilliseconds >= 0d);
    }

    [Fact]
    public void VisibleAwareEarlyExit_IsEnabledWhenMigratingOlderTuning()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = 5,
            EnableVisibleAwareEarlyExit = false,
            VisibleAwareEarlyTerminationMaxCompositeCost = 0d
        };

        tuning.Normalize();

        Assert.Equal(MapStructureRegistrationTuning.CurrentSchemaVersion, tuning.SchemaVersion);
        Assert.True(tuning.EnableVisibleAwareEarlyExit);
        Assert.Equal(0.55d, tuning.VisibleAwareEarlyTerminationMaxCompositeCost, 8);
    }

    // ═══════════════════════════════════════════════════════════════
    // P0-2: Visible-aware 正确性测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void VisibleAware_NoCrashWithNonZeroQueryBounds()
    {
        // P0-2A: When query.Bounds is non-zero and smaller than the full
        // query image, BitwiseAnd must not throw due to mismatched Mat sizes.
        // The live image has content at an offset, so query.Bounds will have
        // non-zero X/Y after bounding box computation.
        using var reference = BuildReference();
        // Create a live image that's a portion of reference, placed within
        // a larger black canvas so the dominant structure cluster is not at (0,0).
        var referenceCrop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, referenceCrop);
        using var live = new Mat(
            new Size(400, 340),
            MatType.CV_8UC3,
            Scalar.Black);
        var livePlacement = new Rect(50, 52, source.Width, source.Height);
        using (var target = new Mat(live, livePlacement))
            source.CopyTo(target);

        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        var expectedOffsetX =
            viewport.X + livePlacement.X - referenceCrop.X;
        var expectedOffsetY =
            viewport.Y + livePlacement.Y - referenceCrop.Y;

        var tuning = TestTuning();
        tuning.EnableVisibleMask = true;
        tuning.EnableVisibleAwareShadow = true;
        tuning.EnableVisibleAwareInjection = true;
        // Lower thresholds so synthetic data passes.
        tuning.VisibleAwareMinimumVisibleStructurePixels = 10;
        tuning.VisibleAwareMinimumVisibleFraction = 0.01d;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        // This MUST NOT throw an OpenCV exception.
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = false
        });

        Assert.True(result.Accepted, result.FailureReason);
    }

    [Fact]
    public void VisibleAware_CandidatePositionNotDoublyOffset()
    {
        // P0-2B: The visible-aware path must not add query.Bounds.X/Y
        // twice to the MatchTemplate position. Verify the resulting
        // transform offset matches the expected (correct) value.
        using var reference = BuildReference();
        var referenceCrop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, referenceCrop);
        // Place the source within a larger canvas at a non-zero position
        // so query.Bounds is non-zero and smaller than the full image.
        using var live = new Mat(
            new Size(400, 340),
            MatType.CV_8UC3,
            Scalar.Black);
        var livePlacement = new Rect(50, 52, source.Width, source.Height);
        using (var target = new Mat(live, livePlacement))
            source.CopyTo(target);

        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        // Correct offset: the live content at (50,52) came from reference
        // at (82,58). Scale=1 so offset = viewport + livePlacement - referenceCrop.
        var expectedOffsetX =
            viewport.X + livePlacement.X - referenceCrop.X;
        var expectedOffsetY =
            viewport.Y + livePlacement.Y - referenceCrop.Y;
        // If Bounds offset were doubled, the result would be off by
        // approximately query.Bounds.X/Y (which will be ~50px each).

        var tuning = TestTuning();
        tuning.EnableVisibleMask = true;
        tuning.EnableVisibleAwareShadow = true;
        tuning.EnableVisibleAwareInjection = true;
        tuning.VisibleAwareMinimumVisibleStructurePixels = 10;
        tuning.VisibleAwareMinimumVisibleFraction = 0.01d;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference, expectedOffsetX + 5d, expectedOffsetY - 5d),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);

        // The offset must NOT be off by ~query.Bounds.X or ~query.Bounds.Y
        // which would indicate double-offset regression.
        var offsetErrorX = Math.Abs(result.Transform.OffsetX - expectedOffsetX);
        var offsetErrorY = Math.Abs(result.Transform.OffsetY - expectedOffsetY);
        Assert.True(offsetErrorX < 20d,
            $"OffsetX error {offsetErrorX:F1}px — double-offset bug would be >40px");
        Assert.True(offsetErrorY < 20d,
            $"OffsetY error {offsetErrorY:F1}px — double-offset bug would be >40px");

        // Extra guard: a double-offset would place the result far from truth.
        // query.Bounds will be at least ~50px in each axis, so a double
        // addition would create errors >= 40px.
        Assert.True(offsetErrorX < 40d,
            $"OffsetX error {offsetErrorX:F1}px >= 40px suggests double-offset regression");
        Assert.True(offsetErrorY < 40d,
            $"OffsetY error {offsetErrorY:F1}px >= 40px suggests double-offset regression");
    }

    [Fact]
    public void VisibleAware_DisabledByDefaultDoesNotInterfere()
    {
        // Sanity check: with visible-aware off (default), the standard
        // registration path still works correctly.
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference, offsetX: expectedOffsetX + 12d, offsetY: expectedOffsetY - 9d),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expectedOffsetY), 0d, 2d);
    }

    private static MapStructureRegistrationTuning TestFastTuning() => new()
    {
        MinimumEdgePixels = 50,
        MinimumSpanPixels = 18,
        MinimumConsistentPartitions = 2,
        TopCandidateCount = 6,
        MaximumChamferPixels = 3.5d,
        MinimumEdgeCoverage = 0.50d,
        MinimumOccupancyCoverage = 0.35d,
        MinimumCandidateMargin = 0.025d,
        ScaleSearchRadius = 0.02d,
        ScaleSearchStep = 0.01d,
        EnableFastAlignment = true
    };

    private static MapOverlayTransform LockedAtScale(
        Mat reference,
        double scale,
        double offsetX = 0d,
        double offsetY = 0d) =>
        new()
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };

    /// <summary>
    /// 生成一个特征明确、适合互逆缩放测试的大型参考图（640×480），
    /// 确保裁切下来的 live 区域远小于降采样后的参考图。
    /// </summary>
    private static Mat BuildLargeReference()
    {
        var image = new Mat(
            new Size(640, 480), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(140, 100, 80, 60), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(280, 80, 130, 70), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(430, 120, 80, 150), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(100, 240, 90, 140), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(280, 220, 120, 120), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(460, 360, 120, 80), Scalar.White, -1);
        Cv2.Line(image, new Point(190, 130), new Point(290, 110),
            Scalar.White, 22);
        Cv2.Line(image, new Point(380, 130), new Point(360, 230),
            Scalar.White, 18);
        Cv2.Line(image, new Point(180, 320), new Point(290, 280),
            Scalar.White, 16);
        Cv2.Line(image, new Point(380, 280), new Point(480, 390),
            Scalar.White, 14);
        Cv2.Circle(image, new Point(340, 280), 30, Scalar.Black, -1);
        return image;
    }

    /// <summary>互逆缩放测试专用 tuning：放宽参数以适应缩放带来的边缘差异。</summary>
    private static MapStructureRegistrationTuning ReciprocalTuning() => new()
    {
        MinimumEdgePixels = 40,
        MinimumSpanPixels = 14,
        MinimumConsistentPartitions = 2,
        TopCandidateCount = 6,
        MaximumChamferPixels = 6.0,
        MinimumEdgeCoverage = 0.22,
        MinimumOccupancyCoverage = 0.22,
        MinimumCandidateMargin = 0.02,
        ScaleSearchRadius = 0.03,
        ScaleSearchStep = 0.01,
        EnableFastAlignment = false,
        FeatureRatioThreshold = 0.78
    };

    // ═══════════════════════════════════════════════════════════════
    // 互逆缩放 (Reciprocal Scale) 测试 — baselineScale < 1.0
    //
    // 注意：当前互逆缩放实现存在已知问题：
    // 1. Fast 路径（TryFastCoarseAlign）无条件激活互逆缩放，
    //    而 Legacy 路径在 RestrictSearchToLockedTransform=true 时跳过。
    //    这一不一致可能导致两条路径对相同输入返回不同结果。
    // 2. 互逆缩放下 CollectCandidates → Evaluate 的坐标映射在
    //    降采样的 referenceDistance 上存在边界越界风险(#OpenCV roi 异常)。
    // 3. Fast 路径的 dsStructure 生命周期管理存在问题，
    //    在 RestrictedSearch 路径中可能出现 ObjectDisposedException。
    // 以下测试重点验证：代码路径可达、无意外崩溃、状态正确重置。
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReciprocalScale_BelowOne_LegacyPath_ExecutesWithoutException()
    {
        // 验证 Legacy 路径在 baselineScale < 1.0 时可到达互逆缩放代码路径。
        // 使用极小的裁剪区域 + 适中的缩比确保 query 远小于降采样参考图。
        using var reference = BuildLargeReference(); // 640×480
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(
            100d, 80d, live.Width, live.Height);
        var tuning = ReciprocalTuning();

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // 核心：不应抛出异常
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        // 无论接受与否，结果对象应完整
        Assert.NotNull(result);
        Assert.True(result.ScaleHypothesisCount > 0);
    }

    [Fact]
    public void ReciprocalScale_FastFallbackToLegacy_ExecutesWithoutDisposedContext()
    {
        // 验证 Fast 路径在 baselineScale < 1.0 时可到达互逆缩放代码路径。
        using var reference = BuildLargeReference();
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(
            100d, 80d, live.Width, live.Height);
        var tuning = ReciprocalTuning();
        tuning.EnableFastAlignment = true;
        // This is the production path that previously reused the fast path's
        // disposed downsampled structure in Legacy.
        tuning.FastFallbackToLegacy = true;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // 核心：不应抛出异常
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        Assert.NotNull(result);
    }

    [Fact]
    public void ReciprocalScale_ContextReset_BetweenSequentialCalls()
    {
        // 连续两次 Register 调用之间，_currentReciprocalScale
        // 应被重置为 None，避免第二次调用泄露第一次的状态。
        // 测试策略：
        //   Call 1: baselineScale < 1.0 → 触发互逆缩放（接受/拒绝均可）
        //   Call 2: baselineScale = 1.0 → 标准 1:1 场景，必须正确通过
        using var reference = BuildLargeReference();
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // Call 1: 互逆缩放路径
        var crop1 = new Rect(200, 150, 100, 80);
        using var source1 = new Mat(reference, crop1);
        const double lowScale = 0.7;
        using var liveSmall = new Mat();
        Cv2.Resize(
            source1,
            liveSmall,
            new Size(
                (int)Math.Round(source1.Width * lowScale),
                (int)Math.Round(source1.Height * lowScale)),
            0d, 0d, InterpolationFlags.Area);

        registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = liveSmall,
                ViewportBounds = new MapScreenRect(
                    100d, 80d, liveSmall.Width, liveSmall.Height),
                LockedTransform = LockedAtScale(reference, lowScale),
                Tuning = ReciprocalTuning(),
                AllowScaleSearch = false
            });

        // Call 2: 标准 1:1 场景 — 不应受 Call 1 的互逆缩放状态影响
        var crop2 = new Rect(200, 150, 180, 130);
        using var liveNormal = new Mat(reference, crop2).Clone();
        var normalViewport = new MapScreenRect(
            0d, 0d, liveNormal.Width, liveNormal.Height);
        var expectedOffsetX = normalViewport.X - crop2.X;
        var expectedOffsetY = normalViewport.Y - crop2.Y;

        var second = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = liveNormal,
                ViewportBounds = normalViewport,
                LockedTransform = Locked(reference),
                Tuning = TestTuning(),
                AllowScaleSearch = false
            });

        Assert.True(second.Accepted,
            $"Second call should succeed regardless of first call's state. "
            + $"Rejection: {second.RejectionReason}");
        Assert.NotNull(second.Transform);
        Assert.InRange(
            Math.Abs(second.Transform.OffsetX - expectedOffsetX),
            0d,
            3d);
        Assert.InRange(
            Math.Abs(second.Transform.OffsetY - expectedOffsetY),
            0d,
            3d);
    }

    [Fact]
    public void ReciprocalScale_FastVsLegacy_RestrictedSearch_BehaviorDocumented()
    {
        // ⚠️ 已知不一致：Fast 路径在 TryFastCoarseAlign 中无条件激活
        // 互逆缩放（line 1121），而 Legacy 路径在 RegisterLegacy 中
        // 会检查 !RestrictSearchToLockedTransform（line 235）。
        //
        // 本测试记录当前行为，当修复后需要更新断言。
        using var reference = BuildLargeReference();
        var crop = new Rect(200, 150, 100, 80);
        using var source = new Mat(reference, crop);
        const double targetScale = 0.7;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Area);

        var viewport = new MapScreenRect(
            100d, 80d, live.Width, live.Height);
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        // Legacy（Fast 禁用，RestrictedSearch）
        var legacyTuning = ReciprocalTuning();
        var legacyResult = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = legacyTuning,
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = true
            });

        // Fast（不允许回退，RestrictedSearch）
        var fastTuning = ReciprocalTuning();
        fastTuning.EnableFastAlignment = true;
        fastTuning.FastFallbackToLegacy = false;
        var fastResult = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = fastTuning,
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = true
            });

        // 记录当前行为。修复 Fast 路径的缺失条件后，两者应一致。
        var bothRejected = !legacyResult.Accepted && !fastResult.Accepted;
        var bothAccepted = legacyResult.Accepted && fastResult.Accepted;
        Assert.True(
            bothRejected || bothAccepted,
            $"Legacy accepted={legacyResult.Accepted} ({legacyResult.RejectionReason}), "
            + $"Fast accepted={fastResult.Accepted} ({fastResult.RejectionReason}). "
            + "Expected consistent accept/reject decision.");
    }

    [Fact]
    public void ReciprocalScale_AboveOne_NotActivated_NormalPathWorks()
    {
        // baselineScale > 1.0 时互逆缩放不应激活，走正常路径。
        using var reference = BuildLargeReference(); // 640×480
        var crop = new Rect(160, 110, 200, 150);
        using var source = new Mat(reference, crop);
        const double targetScale = 1.3;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * targetScale),
                (int)Math.Round(source.Height * targetScale)),
            0d, 0d, InterpolationFlags.Linear);

        var viewport = new MapScreenRect(
            200d, 150d, live.Width, live.Height);
        var tuning = ReciprocalTuning();

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());
        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = LockedAtScale(reference, targetScale),
                Tuning = tuning,
                AllowScaleSearch = true
            });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.InRange(
            result.Transform.ScaleX,
            targetScale - 0.18,
            targetScale + 0.18);
    }
}
