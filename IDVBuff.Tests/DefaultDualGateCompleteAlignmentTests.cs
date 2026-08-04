using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class DefaultDualGateCompleteAlignmentTests
{
    [Fact]
    public async Task DefaultStrategy_ScanAndAlignment_LocksFromDualGateEvidence()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.Both);

        var attempt = scenario.Service.Recognize(
            frame,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(scenario.Map.Id, recognition.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, recognition.Result.Floor);
        Assert.Equal(MapRecognitionSource.Automatic, recognition.Result.Source);
        Assert.Equal(MapAlignmentTrackingMode.GatePairLocked, attempt.Diagnostics.TrackingMode);
        Assert.True(recognition.Result.HasAllRequiredAnchorEvidence);
        Assert.Equal(2, recognition.Result.AnchorMatches.Count);
        Assert.InRange(recognition.Result.Confidence, 0.75d, 1d);
        AssertTransform(recognition.Result.OverlayTransform, 1d, 100d, 80d, 2d);
    }

    [Fact]
    public async Task DefaultStrategy_SideGateOnly_AlignsAfterInitialDualGateLock()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var session = LockWithDefaultStrategy(scenario);
        var crop = new Rect(300, 20, 470, 360);
        var viewport = new MapScreenRect(690d, 310d, crop.Width, crop.Height);
        using var frame = scenario.MainFrame(
            VisibleGates.SideOnly,
            viewport,
            crop);

        var attempt = scenario.Service.AlignSelected(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(1, attempt.Diagnostics.GateCandidateCount);
        Assert.Equal(MapAlignmentTrackingMode.SingleGateTracking, attempt.Diagnostics.TrackingMode);
        Assert.True(attempt.StructureAttempted);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.True(double.IsFinite(recognition.Result.Confidence));
        Assert.InRange(recognition.Result.Confidence, 0d, 1d);
        AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public async Task DefaultStrategy_NoGate_UsesStructureAfterInitialDualGateLock()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var session = LockWithDefaultStrategy(scenario);
        var crop = new Rect(220, 180, 420, 300);
        var viewport = new MapScreenRect(720d, 390d, crop.Width, crop.Height);
        using var frame = scenario.MainFrame(VisibleGates.None, viewport, crop);

        var attempt = scenario.Service.AlignSelected(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
        Assert.Equal(MapRecognitionSource.StructureMatching, recognition.Result.Source);
        Assert.Equal(MapAlignmentTrackingMode.StructureMatched, attempt.Diagnostics.TrackingMode);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public async Task DefaultStrategy_Confidence_UsesSingleGateTrackingFormulaAndRemainsNumeric()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var session = LockWithDefaultStrategy(scenario);

        const double observedGateScore = 0.25d;
        var expectedScale = Assert.IsType<double>(session.GateTemplateScale);
        var observedGateScale = expectedScale * 2d;
        var scaleAgreement = MapAlignmentConfidence.ComputeScaleAgreement(
            observedGateScale,
            expectedScale);
        var confidence = MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            observedGateScore,
            session.LastConfidence,
            scaleAgreement);

        Assert.True(double.IsFinite(confidence));
        Assert.InRange(confidence, 0d, 1d);
        Assert.Equal(
            (observedGateScore * 0.75d)
                + (session.LastConfidence * 0.15d)
                + (scaleAgreement * 0.10d),
            confidence,
            12);
        Assert.True(confidence < MapSessionRules.MediumConfidence);
        Assert.Matches(
            @"\d+\.\d\s*%",
            confidence.ToString("P1", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DefaultStrategy_CompleteAlignment_StaysWithinPerformanceBudget()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.Both);
        var stopwatch = Stopwatch.StartNew();

        var attempt = scenario.Service.Recognize(
            frame,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning);

        stopwatch.Stop();
        Assert.NotNull(attempt.Recognition);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Default dual-gate chain took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
        Assert.True(attempt.Diagnostics.GateDetectionMilliseconds > 0d);
        Assert.True(attempt.Diagnostics.GeometryMilliseconds >= 0d);
    }

    [Fact]
    public async Task DefaultStrategy_CompleteAlignment_BatchLocksEveryTranslatedFrame()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var translations = new (double X, double Y)[]
        {
            (100d, 80d),
            (105d, 83d),
            (110d, 86d),
            (115d, 89d),
            (120d, 92d),
            (125d, 95d)
        };
        var stopwatch = Stopwatch.StartNew();

        foreach (var (x, y) in translations)
        {
            using var frame = scenario.MainFrame(
                VisibleGates.Both,
                new MapScreenRect(x, y, 800d, 600d));
            var attempt = scenario.Service.Recognize(
                frame,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning);
            Assert.True(
                attempt.Recognition is not null,
                $"Frame ({x:F0},{y:F0}) failed: {attempt.FailureReason}");
            var recognition = attempt.Recognition!;
            Assert.Equal(scenario.Map.Id, recognition.Map.Id);
            AssertTransform(recognition.Result.OverlayTransform, 1d, x, y, 2d);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Six default-strategy chains took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
    }

    private static MapAlignmentSession LockWithDefaultStrategy(
        CompleteAlignmentTestScenario scenario)
    {
        using var frame = scenario.MainFrame(VisibleGates.Both);
        var attempt = scenario.Service.Recognize(
            frame,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning);
        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        return MapAlignmentSession.FromRecognition(
            scenario.Map,
            recognition.Result);
    }

    internal static void AssertTransform(
        MapOverlayTransform? transform,
        double scale,
        double offsetX,
        double offsetY,
        double tolerance)
    {
        var actual = Assert.IsType<MapOverlayTransform>(transform);
        Assert.InRange(Math.Abs(actual.ScaleX - scale), 0d, 0.02d);
        Assert.InRange(Math.Abs(actual.ScaleY - scale), 0d, 0.02d);
        Assert.InRange(Math.Abs(actual.OffsetX - offsetX), 0d, tolerance);
        Assert.InRange(Math.Abs(actual.OffsetY - offsetY), 0d, tolerance);
    }
}
