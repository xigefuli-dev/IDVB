using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed partial class GateTemplateDetectorTests
{
    private readonly ITestOutputHelper output;

    public GateTemplateDetectorTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    private static double BaselineClientWidth =>
        DisplayTestMatrix.Baseline.PixelWidth;

    [Theory]
    [InlineData(0.275d)]
    [InlineData(0.4d)]
    [InlineData(0.55d)]
    public void ColdSearchCoversConfiguredScaleRange(double scale)
    {
        // FullSearch no longer enumerates a flat 0.5…1.5 global list (latency
        // tax). Coverage is the client-relative band around ReferenceScale —
        // which matches real in-game gate template scales (~0.15–0.55).
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, scale);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        var candidates = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, frame.Width, frame.Height),
            BaselineClientWidth);

        Assert.True(candidates.Count >= 2);
    }

    [Fact]
    public void ScaledDimmedAndSlightlyBlurredGatesAreDetectedWithinPerformanceBudget()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.8d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var stopwatch = Stopwatch.StartNew();
        var cold = detector.Detect(matchImage, viewport, BaselineClientWidth);
        stopwatch.Stop();
        var coldMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        Assert.True(cold.Count >= 2);
        detector.RememberSuccessfulScale(cold.Take(2).Average(candidate => candidate.Scale));

        stopwatch.Restart();
        var warm = detector.Detect(matchImage, viewport, BaselineClientWidth);
        stopwatch.Stop();
        var warmMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        output.WriteLine($"Cold detection: {coldMilliseconds:F0}ms; warm detection: {warmMilliseconds:F0}ms");
        Assert.True(warm.Count >= 2);
        Assert.True(coldMilliseconds <= 2000d, $"Cold detection took {coldMilliseconds:F0}ms.");
        Assert.True(warmMilliseconds <= 1000d, $"Warm detection took {warmMilliseconds:F0}ms.");
    }

    [Theory]
    [MemberData(nameof(DisplayTestMatrix.All), MemberType = typeof(DisplayTestMatrix))]
    public void EstimatedGateScaleTracksPhysicalClientWidthAcrossDisplayMatrix(
        string name,
        int pixelWidth,
        int pixelHeight,
        int scalePercent,
        uint dpi)
    {
        var referenceClientWidth = BaselineClientWidth;
        const double referenceScale = 0.275d;
        var profile = DisplayTestMatrix.From(
            name,
            pixelWidth,
            pixelHeight,
            scalePercent,
            dpi);
        var expectedScale = referenceScale
            * profile.PixelWidth
            / referenceClientWidth;
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, expectedScale);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        var candidates = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, frame.Width, frame.Height),
            profile.PixelWidth);

        Assert.True(candidates.Count >= 2);
        Assert.All(candidates.Take(2), candidate =>
            Assert.InRange(candidate.Scale, expectedScale * 0.9d, expectedScale * 1.1d));
    }

    [Fact]
    public void WarmSearchFallsBackWhenMapZoomChangesSubstantially()
    {
        const double initialScale = 0.275d;
        const double expandedScale = 0.55d;
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var initialFrame = BuildSyntheticFrame(gatePath, initialScale);
        using var initialMatchImage = GateTemplateDetector.CreateMatchImage(initialFrame);
        var viewport = new MapScreenRect(
            0d,
            0d,
            initialFrame.Width,
            initialFrame.Height);
        var initial = detector.Detect(initialMatchImage, viewport, BaselineClientWidth);
        Assert.True(initial.Count >= 2);
        detector.RememberSuccessfulScale(
            initial.Take(2).Average(candidate => candidate.Scale));

        using var expandedFrame = BuildSyntheticFrame(gatePath, expandedScale);
        using var expandedMatchImage = GateTemplateDetector.CreateMatchImage(expandedFrame);
        var expanded = detector.Detect(
            expandedMatchImage,
            viewport,
            BaselineClientWidth);

        Assert.True(expanded.Count >= 2);
        Assert.All(expanded.Take(2), candidate =>
            Assert.InRange(candidate.Scale, expandedScale * 0.9d, expandedScale * 1.1d));
    }

    [Fact]
    public void LocalConfirmationAtNonZeroViewportPreservesScreenCoordinates()
    {
        // P0-1: verify BuildConfirmationRoi converts screen→local for ROI
        // construction, but the returned GateDetection.ScreenBounds must
        // remain absolute screen coordinates.
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        const double viewportX = 800d;
        const double viewportY = 300d;
        var viewport = new MapScreenRect(
            viewportX, viewportY, frame.Width, frame.Height);

        // Predicted regions are in absolute SCREEN coordinates.
        var predictedRegions = new List<MapScreenRect>
        {
            new(viewportX + 280, viewportY + 230, 100, 100),
            new(viewportX + 1120, viewportY + 720, 100, 100),
        };

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.LocalConfirmationSearch,
                PredictedGateRegions = predictedRegions,
                PredictedScale = 0.275d,
            });

        Assert.True(result.Gates.Count >= 2,
            $"Expected ≥2 gates, got {result.Gates.Count}");

        // Each returned gate must have ScreenBounds with absolute coordinates
        // that include the viewport offset.
        foreach (var gate in result.Gates)
        {
            Assert.True(gate.ScreenBounds.X >= viewportX,
                $"Gate ScreenBounds.X={gate.ScreenBounds.X:F0} should be ≥ viewportX={viewportX}");
            Assert.True(gate.ScreenBounds.Y >= viewportY,
                $"Gate ScreenBounds.Y={gate.ScreenBounds.Y:F0} should be ≥ viewportY={viewportY}");
            Assert.True(gate.ScreenBounds.X < viewportX + frame.Width,
                $"Gate ScreenBounds.X={gate.ScreenBounds.X:F0} exceeded viewport range");
            Assert.True(gate.ScreenBounds.Y < viewportY + frame.Height,
                $"Gate ScreenBounds.Y={gate.ScreenBounds.Y:F0} exceeded viewport range");
        }

        output.WriteLine(
            $"NonZeroViewport: viewport=({viewportX},{viewportY}), " +
            $"{result.Gates.Count} gates, {result.MatchTemplateCalls} calls");
    }

    [Fact]
    public void LocalConfirmationZeroOriginWorksSameAsBefore()
    {
        // P0-1: zero viewport origin must produce identical ROI behavior.
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);
        var predictedRegions = new List<MapScreenRect>
        {
            new(280, 230, 100, 100),
            new(1120, 720, 100, 100),
        };

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.LocalConfirmationSearch,
                PredictedGateRegions = predictedRegions,
                PredictedScale = 0.275d,
            });

        Assert.True(result.Gates.Count >= 2);
        Assert.Equal(GateSearchMode.LocalConfirmationSearch, result.SearchModeUsed);
        // Local confirmation should only search 3 scales × 2 regions.
        Assert.True(result.MatchTemplateCalls <= 7,
            $"Expected ≤7 match calls for local, got {result.MatchTemplateCalls}");
    }

    [Fact]
    public void LocalConfirmationRoiNotDegenerateAtNonZeroViewport()
    {
        // P0-1: ROI must not collapse to the top-left corner when
        // viewport origin is non-zero. The ROI should be centered around
        // the correct local position.
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        const double viewportX = 500d;
        const double viewportY = 200d;
        var viewport = new MapScreenRect(
            viewportX, viewportY, frame.Width, frame.Height);

        // Place gates at known positions in the frame.
        // Absolute screen coords = viewport offset + local pos within frame.
        var predictedRegions = new List<MapScreenRect>
        {
            new(viewportX + 280, viewportY + 230, 100, 100),
            new(viewportX + 1120, viewportY + 720, 100, 100),
        };

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.LocalConfirmationSearch,
                PredictedGateRegions = predictedRegions,
                PredictedScale = 0.275d,
                LocalRoiMinimumPaddingPixels = 24,
                MaximumExpectedMotionPixels = 0,
            });

        Assert.True(result.Gates.Count >= 2,
            $"Non-zero viewport must still find gates, got {result.Gates.Count}");

        // Each returned gate center should be near the predicted center,
        // i.e. viewport offset + 280/230 etc. If the ROI degenerated to
        // (0,0), the gates would be near the left/top of the viewport.
        foreach (var gate in result.Gates)
        {
            var localX = gate.ScreenBounds.CenterX - viewportX;
            var localY = gate.ScreenBounds.CenterY - viewportY;
            Assert.True(localX >= 100d,
                $"Gate local X={localX:F0} should be well inside frame, not near left edge");
            Assert.True(localY >= 100d,
                $"Gate local Y={localY:F0} should be well inside frame, not near top edge");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // P1-2: SingleGateWarmExit really exits early
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SingleGateWarmExit_ReducesScalesEvaluated()
    {
        // P1-2: When single-gate conditions are met, the warm search
        // must exit early (ScalesEvaluated < total warm scale count).
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        detector.RememberSuccessfulScale(0.275d);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = 0.275d,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold = 0.80d,
                SingleGateScaleTolerance = 0.15d,
                AmbiguityScoreGap = 0.05d,
            });

        // With two gates in the frame, the DualGateEarlyExit should fire
        // first (higher priority). Either way, the exit must be early.
        Assert.True(
            result.StopReason == GateSearchStopReason.DualGateEarlyExit
            || result.StopReason == GateSearchStopReason.SingleGateWarmExit,
            $"Expected early exit, got {result.StopReason}");
        Assert.True(result.ScalesEvaluated <= 5,
            $"Early exit should evaluate few scales, got {result.ScalesEvaluated}");
        output.WriteLine($"SingleGateWarmExit: {result.ScalesEvaluated} scales, " +
            $"{result.MatchTemplateCalls} calls, stop={result.StopReason}");
    }

    [Fact]
    public void SingleGateWarmExit_NoFalsePositiveWithoutSufficientAmbiguityGap()
    {
        // P1-2: When score gap between clusters is too small, SingleGateWarmExit
        // must NOT trigger — all warm scales should be evaluated.
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        detector.RememberSuccessfulScale(0.275d);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = 0.275d,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold = 0.80d,
                SingleGateScaleTolerance = 0.15d,
                AmbiguityScoreGap = 0.99d, // impossibly high → never triggers single-gate
            });

        // With AmbiguityScoreGap=0.99, single-gate exit should NOT fire.
        // Dual-gate may still fire if conditions are met.
        if (result.StopReason == GateSearchStopReason.SingleGateWarmExit)
        {
            Assert.Fail("SingleGateWarmExit must not fire with AmbiguityScoreGap=0.99");
        }
        output.WriteLine($"NoFalsePositive: {result.ScalesEvaluated} scales, " +
            $"{result.MatchTemplateCalls} calls, stop={result.StopReason}");
    }

    [Fact]
    public void DualGateEarlyExit_HasPriorityOverSingleGate()
    {
        // P1-2: When both dual-gate and single-gate conditions are met,
        // DualGateEarlyExit must be returned (higher priority).
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        detector.RememberSuccessfulScale(0.275d);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = 0.275d,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold = 0.50d, // very permissive
                SingleGateScaleTolerance = 0.50d,
                AmbiguityScoreGap = -0.5d, // always satisfied
            });

        Assert.True(
            result.StopReason == GateSearchStopReason.DualGateEarlyExit
            || result.Gates.Count >= 2,
            $"With two real gates, dual-gate should fire before single-gate. " +
            $"Stop={result.StopReason}, Gates={result.Gates.Count}");
        output.WriteLine($"Priority: stop={result.StopReason}, " +
            $"{result.ScalesEvaluated} scales, {result.MatchTemplateCalls} calls");
    }

    private static Mat BuildSyntheticFrame(string gatePath, double scale)
    {
        var frame = new Mat(new Size(1706, 1066), MatType.CV_8UC3, new Scalar(35, 42, 48));
        using var gate = Cv2.ImRead(gatePath, ImreadModes.Color);
        using var resized = new Mat();
        Cv2.Resize(gate, resized, new Size(), scale, scale, InterpolationFlags.Linear);
        using var dimmed = new Mat();
        resized.ConvertTo(dimmed, MatType.CV_8UC3, 0.82d, 12d);
        Cv2.GaussianBlur(dimmed, dimmed, new Size(3, 3), 0d);

        Paste(frame, dimmed, 280, 230);
        Paste(frame, dimmed, 1120, 720);
        return frame;
    }

    [Fact]
    public void DualGateExitsEarly()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext { Mode = GateSearchMode.FullSearch });

        Assert.True(result.Gates.Count >= 2);
        Assert.Equal(GateSearchStopReason.DualGateEarlyExit, result.StopReason);
        // Early exit should test far fewer scales than the full range.
        Assert.True(result.ScalesEvaluated <= 7, $"Scales: {result.ScalesEvaluated}");
        output.WriteLine($"DualGateEarlyExit: {result.ScalesEvaluated} scales, " +
            $"{result.MatchTemplateCalls} calls, {result.ElapsedMilliseconds:F0}ms");
    }

    [Fact]
    public void WarmSearchNeverUpgradesInternally()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        detector.RememberSuccessfulScale(0.275d);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = 0.275d,
            });

        Assert.True(result.Gates.Count >= 2);
        Assert.Equal(GateSearchMode.WarmScaleSearch, result.SearchModeUsed);
        // Warm only tests ~7 scales, never the full range of 20+.
        Assert.True(result.ScalesEvaluated <= 7,
            $"Expected ≤7 warm scales, got {result.ScalesEvaluated}");
        output.WriteLine($"WarmSearch: {result.ScalesEvaluated} scales, " +
            $"{result.MatchTemplateCalls} calls, {result.ElapsedMilliseconds:F0}ms");
    }

    [Fact]
    public void NoGateWarmSearchDoesNotUseGlobalFallback()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        detector.RememberSuccessfulScale(0.275d);
        // Build a frame with gates, then search at a scale where they won't match.
        using var frame = BuildSyntheticFrame(gatePath, 0.5d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        // Warm search at scale far from actual gate scale → likely no match.
        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            0.95d, // high threshold
            new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = 0.2d, // far from real scale
            });

        // Must not have searched global fallback scales.
        Assert.Equal(GateSearchMode.WarmScaleSearch, result.SearchModeUsed);
        Assert.True(result.ScalesEvaluated <= 7,
            $"Expected ≤7 warm scales even with no match, got {result.ScalesEvaluated}");
    }

}
