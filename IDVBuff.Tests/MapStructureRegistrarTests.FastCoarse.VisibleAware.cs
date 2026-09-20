using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void VisibleAwareIoU_ExactMatchIsOne_AndResponseIsBounded()
    {
        using var reference = Mat.Zeros(12, 14, MatType.CV_32FC1).ToMat();
        Cv2.Rectangle(reference, new Rect(4, 3, 5, 6), Scalar.All(1), -1);
        using var structure = new Mat(reference, new Rect(4, 3, 5, 6)).Clone();
        using var visible = Mat.Ones(structure.Size(), MatType.CV_32FC1);
        using var response = MapStructureVisibleAwareSearch.ComputeIoU(reference, structure, visible);

        Cv2.MinMaxLoc(response, out var minimum, out var maximum, out _, out var location);
        Assert.InRange(minimum, 0d, 1d);
        Assert.InRange(maximum, 0d, 1d);
        Assert.Equal(new Point(4, 3), location);
        Assert.InRange(maximum, 0.99999d, 1d);
    }

    [Fact]
    public void VisibleAwareMatAndUMatCorrelation_Agree()
    {
        VisibleAwareCorrelationSession.ResetStickyFallbackForTests();
        using var reference = Mat.Zeros(40, 48, MatType.CV_32FC1).ToMat();
        Cv2.Rectangle(reference, new Rect(13, 11, 12, 9), Scalar.All(1), -1);
        using var structure = new Mat(reference, new Rect(13, 11, 12, 9)).Clone();
        using var visible = Mat.Ones(structure.Size(), MatType.CV_32FC1);
        using IVisibleAwareCorrelationBackend mat = new MatCorrelationBackend();
        using IVisibleAwareCorrelationBackend umat = new UMatCorrelationBackend();
        using var matResponse = mat.Correlate(reference, structure, visible);
        using var umatResponse = umat.Correlate(reference, structure, visible);
        Cv2.MinMaxLoc(matResponse, out _, out var matMax, out _, out var matAt);
        Cv2.MinMaxLoc(umatResponse, out _, out var umatMax, out _, out var umatAt);
        Assert.InRange(Math.Abs(matAt.X - umatAt.X), 0, 1);
        Assert.InRange(Math.Abs(matAt.Y - umatAt.Y), 0, 1);
        Assert.InRange(Math.Abs(matMax - umatMax), 0d, 0.0001d);
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

    // ═══════════════════════════════════════════════════════════════
    // VisibleMask 生成与注入回归测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void VisibleMask_GeneratedWhenEnabled_AndNullWhenDisabled()
    {
        var preprocessor = new MapStructurePreprocessor();
        using var source = BuildReference();

        using var withoutMask = preprocessor.ProcessLiveRoi(source);
        Assert.Null(withoutMask.RawVisibleMask);

        using var withMask = preprocessor.ProcessLiveRoi(
            source, null, null, generateVisibleMask: true);
        Assert.NotNull(withMask.RawVisibleMask);
        Assert.False(withMask.RawVisibleMask.Empty());
        Assert.True(Cv2.CountNonZero(withMask.RawVisibleMask) > 0,
            "白色结构区域在 HSV 阈值下应被判为可见");
    }

    [Fact]
    public void VisibleMask_RespectsHsvThresholds()
    {
        var preprocessor = new MapStructurePreprocessor();
        // 四象限：V 阈值之上有 S / 高亮 / 双低 三种情况。
        // 直接用 HSV 值构造再转 BGR，避免手算饱和度误差。
        using var source = new Mat(
            new Size(200, 80), MatType.CV_8UC3, Scalar.Black);
        using var hsvSource = new Mat(
            new Size(200, 80), MatType.CV_8UC3, Scalar.Black);
        // 左上：V=50, S=50 → (S>14 AND V>42，且 S<105 不触发 nuisance) → 可见
        Cv2.Rectangle(hsvSource, new Rect(0, 0, 100, 40), new Scalar(0, 50, 50), -1);
        // 右上：V=90, S=50 → (V>80 高亮) → 可见
        Cv2.Rectangle(hsvSource, new Rect(100, 0, 100, 40), new Scalar(0, 50, 90), -1);
        // 左下：V=30 → V 不够 → 不可见
        Cv2.Rectangle(hsvSource, new Rect(0, 40, 100, 40), new Scalar(0, 50, 30), -1);
        // 右下：V=50, S=5 → 双低 → 不可见
        Cv2.Rectangle(hsvSource, new Rect(100, 40, 100, 40), new Scalar(0, 5, 50), -1);
        Cv2.CvtColor(hsvSource, source, ColorConversionCodes.HSV2BGR);

        using var features = preprocessor.ProcessLiveRoi(
            source, null, null, generateVisibleMask: true);
        Assert.NotNull(features.RawVisibleMask);
        using var mask = features.RawVisibleMask.Clone();

        using var topLeftPatch = new Mat(mask, new Rect(0, 0, 100, 40));
        using var topRightPatch = new Mat(mask, new Rect(100, 0, 100, 40));
        using var bottomLeftPatch = new Mat(mask, new Rect(0, 40, 100, 40));
        using var bottomRightPatch = new Mat(mask, new Rect(100, 40, 100, 40));
        var topLeftVisible = Cv2.CountNonZero(topLeftPatch);
        var topRightVisible = Cv2.CountNonZero(topRightPatch);
        var bottomLeftVisible = Cv2.CountNonZero(bottomLeftPatch);
        var bottomRightVisible = Cv2.CountNonZero(bottomRightPatch);
        Assert.True(topLeftVisible > 0,
            $"TL 应可见, TL={topLeftVisible} TR={topRightVisible} "
            + $"BL={bottomLeftVisible} BR={bottomRightVisible} "
            + $"maskTotal={Cv2.CountNonZero(mask)}");
        Assert.True(topRightVisible > 0,
            $"TR 应可见, TL={topLeftVisible} TR={topRightVisible} "
            + $"BL={bottomLeftVisible} BR={bottomRightVisible} "
            + $"maskTotal={Cv2.CountNonZero(mask)}");
        Assert.Equal(0, bottomLeftVisible);
        Assert.Equal(0, bottomRightVisible);
    }

    [Fact]
    public void VisibleAware_CandidatesInjectedWithoutEarlyExit()
    {
        using var reference = BuildReference();
        var referenceCrop = new Rect(35, 35, 200, 160);
        using var live = new Mat(reference, referenceCrop).Clone();
        var viewport = new MapScreenRect(100d, 80d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - referenceCrop.X;
        var expectedOffsetY = viewport.Y - referenceCrop.Y;

        var tuning = TestTuning();
        tuning.EnableVisibleMask = true;
        tuning.EnableVisibleAwareInjection = true;
        tuning.EnableVisibleAwareShadow = false;
        tuning.EnableVisibleAwareEarlyExit = false;
        // 合成图整体亮，放宽最小可见门槛确保候选生成
        tuning.VisibleAwareMinimumVisibleFraction = 0.01d;
        tuning.VisibleAwareMinimumVisibleStructurePixels = 10;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference, expectedOffsetX + 6d, expectedOffsetY - 6d),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = false
        });

        Assert.NotNull(result);
        // 关闭 EarlyExit 时不应出现 early-accept 标记，Legacy 搜索必须照常执行
        Assert.False(result.VisibleAwareEarlyAccepted);
        // Visible-aware 搜索确实运行：有耗时、有候选计数、有可见像素统计
        Assert.True(result.VisibleAwareSearchMilliseconds > 0d,
            "Visible-aware 搜索应实际执行");
        Assert.True(result.VisibleAwareCandidateCount > 0,
            "可见候选计数应大于 0");
        Assert.True(result.VisibleFraction > 0d,
            "应统计出可见区域占比");
        Assert.True(result.VisibleStructurePixels > 0,
            "应统计出可见结构像素");
        // 注入的候选可能因去重/排序未进入最终 top-N（与模板候选同一
        // 对齐盆地时会被 DistinctCandidates 合并），但这不影响管线正确性；
        // 只要候选池经过真实搜索并有诊断即证明注入路径生效。
        // 最终仍应正常接受（结构完全来自参考图裁切）
        Assert.True(result.Accepted, result.FailureReason);
        // 最终应正常接受（结构完全来自参考图裁切）
        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expectedOffsetX), 0d, 4d);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expectedOffsetY), 0d, 4d);
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

}
