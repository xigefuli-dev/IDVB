using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed partial class GateTemplateDetectorTests
{
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
        // Full search evaluates the client-relative band (global 0.5…1.5 list
        // was removed as a latency tax). Still multi-scale cold start.
        Assert.True(result.ScalesEvaluated >= 8,
            $"Expected ≥8 scales for full search, got {result.ScalesEvaluated}");
        Assert.True(result.ScalesEvaluated <= 15,
            $"Expected ≤15 scales after FullSearch list trim, got {result.ScalesEvaluated}");
        output.WriteLine($"ColdStartFullSearch (no gates): {result.ScalesEvaluated} scales, " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public void FullSearchSingleGateCanExitEarlyWhenEnabled()
    {
        var gatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        using var detector = new GateTemplateDetector(gatePath);
        // One gate only — dual-gate early exit cannot fire.
        using var frame = BuildSingleGateFrame(gatePath, 0.275d);
        using var matchImage = GateTemplateDetector.CreateMatchImage(frame);
        var viewport = new MapScreenRect(0d, 0d, frame.Width, frame.Height);

        var result = detector.Detect(
            matchImage,
            viewport,
            BaselineClientWidth,
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold =
                    GateTemplateRules.EarlyExitScoreThreshold,
            });

        Assert.True(result.Gates.Count >= 1);
        Assert.Equal(
            GateSearchStopReason.SingleGateWarmExit,
            result.StopReason);
        Assert.True(
            result.ScalesEvaluated
                >= GateTemplateRules.FullSearchMinScalesBeforeSingleGateExit,
            $"Must evaluate min scales before single-gate exit, got {result.ScalesEvaluated}");
        Assert.True(
            result.ScalesEvaluated < 12,
            $"Single-gate early exit should not burn the full list, got {result.ScalesEvaluated}");
        output.WriteLine(
            $"FullSearchSingleGateExit: {result.ScalesEvaluated} scales, "
            + $"{result.ElapsedMilliseconds:F0}ms, stop={result.StopReason}");
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

    private static Mat BuildSingleGateFrame(string gatePath, double scale)
    {
        var frame = new Mat(new Size(1706, 1066), MatType.CV_8UC3, new Scalar(35, 42, 48));
        using var gate = Cv2.ImRead(gatePath, ImreadModes.Color);
        using var resized = new Mat();
        Cv2.Resize(gate, resized, new Size(), scale, scale, InterpolationFlags.Linear);
        using var dimmed = new Mat();
        resized.ConvertTo(dimmed, MatType.CV_8UC3, 0.82d, 12d);
        Cv2.GaussianBlur(dimmed, dimmed, new Size(3, 3), 0d);
        Paste(frame, dimmed, 280, 230);
        return frame;
    }

    private static void Paste(Mat target, Mat source, int x, int y)
    {
        using var region = new Mat(target, new Rect(x, y, source.Width, source.Height));
        source.CopyTo(region);
    }
}
