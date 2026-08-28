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
    public void RecoveryTuning_DisablesFastAndScaleEarlyStop()
    {
        var original = new MapStructureRegistrationTuning
        {
            EnableFastAlignment = false,
            DisableScaleEarlyTermination = true
        };

        var clone = original.Clone();
        clone.Normalize();

        Assert.False(clone.EnableFastAlignment);
        Assert.True(clone.DisableScaleEarlyTermination);
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
    public void FastCoarseAlign_LongThinQueryStillProducesCandidates()
    {
        using var reference = new Mat(
            new Size(1000, 220), MatType.CV_8UC3, Scalar.Black);
        for (var x = 40; x < 940; x += 90)
        {
            Cv2.Rectangle(reference, new Rect(x, 75, 55, 45), Scalar.White, -1);
            Cv2.Line(reference, new Point(x + 20, 70), new Point(x + 65, 130),
                Scalar.White, 8);
        }

        using var live = new Mat(reference, new Rect(90, 60, 800, 80)).Clone();
        var tuning = TestFastTuning();
        tuning.FastFallbackToLegacy = false;
        tuning.FastCoarseMaxDimension = 200;
        var result = new MapStructureRegistrar(new MapStructurePreprocessor())
            .Register(new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = new MapScreenRect(0d, 0d, live.Width, live.Height),
                LockedTransform = Locked(reference),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        Assert.NotEqual(MapStructureRejectionReason.NoCandidate, result.RejectionReason);
        Assert.True(result.FastCoarseCandidateCount > 0,
            "The fast coarse search must not collapse the short side below its minimum.");
    }

    [Fact]
    public void FastCoarseAlign_RejectsWeakGeometricLockBeforeEarlyAccept()
    {
        var weak = new MapStructureConfidenceBreakdown
        {
            GeometricLockConfidence = 0.59d
        };
        var strong = weak with { GeometricLockConfidence = 0.60d };

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.ValidateFastConfidence(weak, 0.60d));
        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.ValidateFastConfidence(strong, 0.60d));
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

}
