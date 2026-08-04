using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class SideEntranceStrategyCompleteAlignmentTests
{
    [Fact]
    public async Task SideStrategy_ScanAndAlignment_LocksFromSideEntranceIdentity()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, frame);

        var attempt = scenario.Service.AlignSelected(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(scenario.Map.Id, recognition.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, recognition.Result.Floor);
        Assert.Equal(1, attempt.Diagnostics.GateCandidateCount);
        Assert.True(session.SideEntranceScanPriorConfidence > 0.80d);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.True(double.IsFinite(recognition.Result.Confidence));
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            100d,
            80d,
            4d);
    }

    [Fact]
    public async Task SideStrategy_NormalDualGateFrame_UpgradesToGatePairLock()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        using var dualGateFrame = scenario.MainFrame(
            VisibleGates.Both,
            new MapScreenRect(420d, 210d, 800d, 600d));

        var attempt = scenario.Service.AlignSelected(
            dualGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.True(recognition.Result.HasAllRequiredAnchorEvidence);
        Assert.Equal(2, recognition.Result.AnchorMatches.Count);
        Assert.Equal(MapRecognitionSource.SelectedMapGatePair, recognition.Result.Source);
        Assert.Equal(MapAlignmentTrackingMode.GatePairLocked, attempt.Diagnostics.TrackingMode);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            420d,
            210d,
            2d);
    }

    [Fact]
    public async Task SideStrategy_SingleGateFrame_AlignsWithSidePriorAndStructure()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        var crop = new Rect(300, 20, 470, 360);
        var viewport = new MapScreenRect(750d, 330d, crop.Width, crop.Height);
        using var singleGateFrame = scenario.MainFrame(
            VisibleGates.SideOnly,
            viewport,
            crop);

        var attempt = scenario.Service.AlignSelected(
            singleGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        var recognition = Assert.IsType<RuntimeMapRecognition>(attempt.Recognition);
        Assert.Equal(1, attempt.Diagnostics.GateCandidateCount);
        Assert.Equal(MapAlignmentTrackingMode.SingleGateTracking, attempt.Diagnostics.TrackingMode);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        Assert.InRange(recognition.Result.Confidence, 0d, 1d);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public async Task SideStrategy_NoGateFrame_UsesKnownMapStructureForAlignment()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        var crop = new Rect(220, 180, 420, 300);
        var viewport = new MapScreenRect(760d, 420d, crop.Width, crop.Height);
        using var noGateFrame = scenario.MainFrame(
            VisibleGates.None,
            viewport,
            crop);

        var attempt = scenario.Service.AlignSelected(
            noGateFrame,
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
        Assert.True(session.SideEntranceScanPriorConfidence > 0d);
        DefaultDualGateCompleteAlignmentTests.AssertTransform(
            recognition.Result.OverlayTransform,
            1d,
            viewport.X - crop.X,
            viewport.Y - crop.Y,
            4d);
    }

    [Fact]
    public void SideStrategy_Confidence_UsesSideSpecificNumericFormulas()
    {
        const double prior = 0.58d;
        const double gateScore = 0.36d;
        const double scaleAgreement = 0.20d;
        var singleGate = MapAlignmentConfidence.ComputeSideEntranceSingleGateConfidence(
            prior,
            gateScore,
            scaleAgreement);
        var structure = MapAlignmentConfidence.ComputeSideEntranceStructureConfidence(
            prior,
            locationQuality: 0.42d,
            candidateSeparation: 0.20d,
            featureConsensus: -1d,
            refinementQuality: -1d);

        Assert.Equal(0.432d, singleGate, 12);
        Assert.Equal((prior * 0.35d + 0.42d * 0.30d + 0.20d * 0.15d) / 0.80d,
            structure,
            12);
        Assert.All(
            new[] { singleGate, structure },
            confidence =>
            {
                Assert.True(double.IsFinite(confidence));
                Assert.InRange(confidence, 0d, 1d);
                Assert.Matches(
                    @"\d+\.\d\s*%",
                    confidence.ToString(
                        "P1",
                        System.Globalization.CultureInfo.InvariantCulture));
            });
    }

    [Fact]
    public async Task SideStrategy_CompleteAlignment_StaysWithinPerformanceBudget()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var stopwatch = Stopwatch.StartNew();

        var session = SeedWithSideEntranceStrategy(scenario, frame);
        var attempt = scenario.Service.AlignSelected(
            frame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning);

        stopwatch.Stop();
        Assert.NotNull(attempt.Recognition);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(7),
            $"Side-strategy chain took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
        Assert.True(attempt.Diagnostics.GateDetectionMilliseconds > 0d);
        Assert.True(attempt.Diagnostics.StructureSearchMilliseconds >= 0d);
    }

    [Fact]
    public async Task SideStrategy_CompleteAlignment_BatchHandlesGateVisibilityChanges()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var samples = new[]
        {
            (X: 100d, Y: 80d, Gates: VisibleGates.SideOnly),
            (X: 360d, Y: 180d, Gates: VisibleGates.Both),
            (X: 620d, Y: 300d, Gates: VisibleGates.MainOnly),
            (X: 900d, Y: 440d, Gates: VisibleGates.None)
        };
        var stopwatch = Stopwatch.StartNew();

        foreach (var sample in samples)
        {
            using var scanFrame = scenario.MainFrame(
                VisibleGates.SideOnly,
                new MapScreenRect(sample.X, sample.Y, 800d, 600d));
            var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
            using var alignmentFrame = scenario.MainFrame(
                sample.Gates,
                new MapScreenRect(sample.X, sample.Y, 800d, 600d));
            var attempt = scenario.Service.AlignSelected(
                alignmentFrame,
                scenario.Map.Id,
                session,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning,
                CompleteAlignmentTestScenario.StructureTuning);

            Assert.True(
                attempt.Recognition is not null,
                $"{sample.Gates} frame failed: {attempt.FailureReason}");
            DefaultDualGateCompleteAlignmentTests.AssertTransform(
                attempt.Recognition!.Result.OverlayTransform,
                1d,
                sample.X,
                sample.Y,
                4d);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Four side-strategy chains took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
    }

    private static MapAlignmentSession SeedWithSideEntranceStrategy(
        CompleteAlignmentTestScenario scenario,
        CapturedGameFrame frame)
    {
        var candidates = scenario.Service.RunSideEntranceScan(frame.Image, topK: 3);
        var candidate = Assert.Single(candidates);
        Assert.Equal(scenario.Map.Id, candidate.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, candidate.FloorKey);
        Assert.True(candidate.MatchScore > 0.80d);
        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            frame.ViewportBounds,
            out var session,
            out var failureReason);
        Assert.True(created, failureReason);
        Assert.False(session.HasGatePairLock);
        Assert.True(session.SideEntranceScanPriorConfidence > 0d);
        return session;
    }
}
