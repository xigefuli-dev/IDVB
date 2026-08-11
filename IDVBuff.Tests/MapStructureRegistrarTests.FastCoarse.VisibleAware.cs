using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
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

}
