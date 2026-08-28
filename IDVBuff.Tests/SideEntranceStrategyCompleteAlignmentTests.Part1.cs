using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;public sealed partial class SideEntranceStrategyCompleteAlignmentTests
{

    /// <summary>
    /// Regression: once the side gate is not positively identified, the
    /// already-selected map must use ordinary structure alignment. In
    /// particular, a no-gate frame must not inherit the side-route scale band
    /// or restricted search basin.
    /// </summary>
    [Fact]
    public async Task SideStrategy_NoGateFallback_UsesOrdinaryStructureAlignment()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var scanFrame = scenario.MainFrame(VisibleGates.SideOnly);
        var session = SeedWithSideEntranceStrategy(scenario, scanFrame);
        // Force pure structure (no single-gate proposal) so the search flags
        // under test are the ones used when gate identity fails in production.
        var crop = new Rect(220, 180, 420, 300);
        var viewport = new MapScreenRect(760d, 420d, crop.Width, crop.Height);
        using var noGateFrame = scenario.MainFrame(
            VisibleGates.None,
            viewport,
            crop);

        var structureTuning = CompleteAlignmentTestScenario.StructureTuning;
        // Keep a non-zero tracking scale band so this catches accidental
        // inheritance of the side-route scale search.
        structureTuning.TrackingScaleSearchRadius = 0.02d;
        structureTuning.ScaleSearchStep = 0.01d;
        structureTuning.Normalize();

        var attempt = scenario.Service.AlignSideEntrance(
            noGateFrame,
            scenario.Map.Id,
            session,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            structureTuning);

        var structure = Assert.IsType<MapStructureRegistrationResult>(
            attempt.StructureResult);
        Assert.True(
            attempt.StructureAccepted,
            attempt.StructureFailureReason);
        Assert.True(
            structure.ScaleHypothesisCount == 1,
            "A no-gate frame must use the ordinary fixed-scale structure path "
            + $"(got ScaleHypothesisCount={structure.ScaleHypothesisCount}).");
        Assert.False(
            structure.UsedRestrictedSearch,
            "The changed viewport should use the global recovery branch "
            + "after local structure search fails.");
        Assert.Equal(
            MapRecognitionSource.StructureMatching,
            attempt.Recognition!.Result.Source);
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
