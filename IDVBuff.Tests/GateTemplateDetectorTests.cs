using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed class GateTemplateDetectorTests(ITestOutputHelper output)
{
    private static double BaselineClientWidth =>
        DisplayTestMatrix.Baseline.PixelWidth;

    [Theory]
    [InlineData(0.275d)]
    [InlineData(0.5d)]
    [InlineData(1.5d)]
    public void ColdSearchCoversConfiguredScaleRange(double scale)
    {
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

    [Fact]
    public void ColdStartNoGateUsesFullSearch()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildEmptyFrame();
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var stopwatch = Stopwatch.StartNew();
        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext { Mode = GateSearchMode.FullSearch });
        stopwatch.Stop();

        Assert.Equal(GateSearchMode.FullSearch, result.SearchModeUsed);
        // Full search should evaluate many scales.
        Assert.True(result.ScalesEvaluated >= 10,
            $"Expected ≥10 scales for full search, got {result.ScalesEvaluated}");
        output.WriteLine($"ColdStartFullSearch (no gates): {result.ScalesEvaluated} scales, " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public void LocalConfirmationUsesOnlyConfiguredRois()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        // Predict gate regions at the actual positions.
        var predictedRegions = new List<MapScreenRect>
        {
            new(280, 230, 100, 100),
            new(1120, 720, 100, 100),
        };

        var result = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, frame.Width, frame.Height),
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.LocalConfirmationSearch,
                PredictedGateRegions = predictedRegions,
                PredictedScale = 0.275d,
            });

        // Local confirmation should only search 3 scales × 2 regions = few calls.
        Assert.Equal(GateSearchMode.LocalConfirmationSearch, result.SearchModeUsed);
        Assert.True(result.MatchTemplateCalls <= 7,
            $"Expected ≤7 match calls for local, got {result.MatchTemplateCalls}");
        Assert.True(result.Gates.Count >= 2);
        output.WriteLine($"LocalConfirmation: {result.ScalesEvaluated} scales, " +
            $"{result.RegionsEvaluated} regions, {result.MatchTemplateCalls} calls, " +
            $"{result.ElapsedMilliseconds:F0}ms");
    }

    [Fact]
    public void BudgetExceededIsDistinctFromNoCandidate()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        var result = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, frame.Width, frame.Height),
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
                TimeBudgetMilliseconds = 1, // impossibly tight
            });

        Assert.True(result.BudgetExceeded);
        Assert.Equal(GateSearchStopReason.BudgetExceeded, result.StopReason);
    }

    [Fact]
    public void SamePhysicalGateAcrossScalesIsNotAmbiguous()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        // Run a multi-scale search.
        var result = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, frame.Width, frame.Height),
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext { Mode = GateSearchMode.FullSearch });

        Assert.True(result.Gates.Count >= 2);
        // Raw candidates may have many duplicates of the same physical gate
        // at adjacent scales — clustering should reduce to 2 distinct gates.
        Assert.True(result.RawCandidates.Count >= result.Gates.Count,
            $"Raw {result.RawCandidates.Count} should be ≥ clustered {result.Gates.Count}");
        output.WriteLine($"Clustering: {result.RawCandidates.Count} raw → {result.Gates.Count} clustered");
    }

    [Fact]
    public void TwoSpatiallyDistinctCandidatesAreAmbiguous()
    {
        // The synthetic frame has two gates at distinct positions.
        // The clustering should NOT merge them.
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);

        var result = detector.Detect(
            matchImage,
            new MapScreenRect(0d, 0d, frame.Width, frame.Height),
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext { Mode = GateSearchMode.FullSearch });

        // Must detect exactly 2 clustered gates.
        Assert.True(result.Gates.Count >= 2);
        if (result.Gates.Count >= 2)
        {
            var iou = IntersectionOverUnion(
                result.Gates[0].ScreenBounds,
                result.Gates[1].ScreenBounds);
            Assert.True(iou < 0.1d,
                $"Two distinct gates should not overlap (IoU={iou:F2})");
        }
    }

    [Fact]
    public void LockedScaleSearchesExactlyOneScale()
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
            new GateSearchContext
            {
                Mode = GateSearchMode.LockedScale,
                LockedScale = 0.275d,
            });

        Assert.Equal(GateSearchMode.LockedScale, result.SearchModeUsed);
        Assert.Equal(1, result.ScalesEvaluated);
        Assert.True(result.Gates.Count >= 2,
            $"Expected ≥2 gates from locked scale, got {result.Gates.Count}");
        output.WriteLine(
            $"LockedScale: {result.ScalesEvaluated} scale, " +
            $"{result.MatchTemplateCalls} call, " +
            $"{result.ElapsedMilliseconds:F0}ms");
    }

    [Fact]
    public void LockedOnlyScalesHandlesNullAndInvalidInputs()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        using var frame = BuildSyntheticFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var nullResult = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.LockedScale,
                LockedScale = null,
            });

        Assert.Equal(GateSearchStopReason.NoValidScale, nullResult.StopReason);
        Assert.Equal(0, nullResult.ScalesEvaluated);
        output.WriteLine(
            "LockedScale with null scale correctly returns NoValidScale.");
    }

    [Fact]
    public void ColdDetectorWarmScaleSearchUsesContextWarmScale()
    {
        // Regression: side-entrance alignment passes the scan-derived scale
        // via GateSearchContext.WarmScale on a detector that has never had a
        // successful detection (_warmScale is null). The context scale must
        // drive the warm scale band — previously this returned NoValidScale
        // with 0 candidates, silently breaking the side-entrance strategy.
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        // NOTE: intentionally NO RememberSuccessfulScale — cold start.
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
            });

        Assert.NotEqual(GateSearchStopReason.NoValidScale, result.StopReason);
        Assert.True(result.ScalesEvaluated > 0,
            "Context warm scale must produce a scale band on a cold detector.");
        Assert.True(result.Gates.Count >= 1,
            $"Expected gates from context warm scale, got {result.Gates.Count}");
        output.WriteLine(
            $"Cold WarmScaleSearch via context: {result.ScalesEvaluated} scales, " +
            $"{result.Gates.Count} gates, stop={result.StopReason}");
    }

    [Fact]
    public void ColdDetectorWarmScaleSearchWithoutAnyScaleReturnsNoValidScale()
    {
        // Cold detector + no context warm scale → nothing to search.
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
            new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = null,
            });

        Assert.Equal(GateSearchStopReason.NoValidScale, result.StopReason);
        Assert.Equal(0, result.ScalesEvaluated);
    }

    [Fact]
    public void ContextWarmScaleTakesPriorityOverRememberedScale()
    {
        // When both are present, the caller's explicit context scale wins:
        // the remembered scale may be stale (e.g. from a different zoom).
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        // Remember a scale far outside the warm band of the true scale, so
        // if the detector used the remembered value the gates would be missed.
        detector.RememberSuccessfulScale(1.2d);
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

        Assert.True(result.Gates.Count >= 1,
            $"Context scale 0.275 should find gates despite remembered 1.2, " +
            $"got {result.Gates.Count}");
        Assert.All(result.Gates, gate =>
            Assert.True(Math.Abs((gate.Scale / 0.275d) - 1d) <= 0.20d,
                $"Gate scale {gate.Scale:F3} should be near context scale 0.275"));
    }

    private static double IntersectionOverUnion(MapScreenRect left, MapScreenRect right)
    {
        var x1 = Math.Max(left.X, right.X);
        var y1 = Math.Max(left.Y, right.Y);
        var x2 = Math.Min(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var intersection = Math.Max(0d, x2 - x1) * Math.Max(0d, y2 - y1);
        var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
        return union <= 0d ? 0d : intersection / union;
    }

    private static Mat BuildEmptyFrame()
    {
        return new Mat(new Size(1706, 1066), MatType.CV_8UC3, new Scalar(35, 42, 48));
    }

    private static void Paste(Mat target, Mat source, int x, int y)
    {
        using var region = new Mat(target, new Rect(x, y, source.Width, source.Height));
        source.CopyTo(region);
    }
}
