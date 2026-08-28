using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceStrategyCompleteAlignmentTests
{
    [Fact]
    public async Task InitialSideCandidate_ReusesItsCurrentScanGateEvidence()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var scan = scenario.Service.RunSideEntranceScan(
            frame,
            CompleteAlignmentTestScenario.RecognitionTuning,
            topK: 5,
            mapClass: scenario.Map.Class);
        var candidate = Assert.Single(scan.Candidates);
        Assert.True(scenario.Service.TryCreateSideEntranceAlignmentSeed(
            candidate,
            frame.ViewportBounds,
            out var seed,
            out var seedFailure), seedFailure);
        var context = new AlignmentSearchContext
        {
            UseRestrictedStructureFallback = true,
            UseInitialHighPrecisionRecovery = true,
            GateSearch = new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = seed.GateTemplateScale
            }
        };

        var attempt = scenario.Service.AlignSideEntrance(
            frame,
            scenario.Map.Id,
            seed,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning,
            alignmentSearchContext: context,
            mapClass: scenario.Map.Class);

        Assert.Single(attempt.GateDetectionResult!.Gates);
        Assert.Equal(0d, attempt.Diagnostics.GateDetectionMilliseconds);
        Assert.True(attempt.StructureAttempted);
    }
}
