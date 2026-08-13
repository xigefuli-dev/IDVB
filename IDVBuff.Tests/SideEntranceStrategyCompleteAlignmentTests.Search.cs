using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceStrategyCompleteAlignmentTests
{
    [Fact]
    public async Task SideScan_InitialFullSearchDoesNotUseSingleGateWarmExit()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);

        var scan = scenario.Service.RunSideEntranceScan(
            frame,
            CompleteAlignmentTestScenario.RecognitionTuning,
            topK: 5,
            mapClass: scenario.Map.Class);

        Assert.Equal(
            GateSearchMode.FullSearch,
            scan.GateDetection.SearchModeUsed);
        Assert.Equal(
            GateSearchStopReason.Completed,
            scan.GateDetection.StopReason);
        Assert.NotEqual(
            GateSearchStopReason.SingleGateWarmExit,
            scan.GateDetection.StopReason);
        Assert.True(
            scan.GateDetection.ScalesEvaluated >= 8,
            "Initial side scan must evaluate the complete multi-scale schedule; "
            + $"got {scan.GateDetection.ScalesEvaluated} scales.");
    }

    [Fact]
    public async Task SideScan_InitialFullSearchDoesNotUseDualGateEarlyExit()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        using var frame = scenario.MainFrame(VisibleGates.Both);

        var scan = scenario.Service.RunSideEntranceScan(
            frame,
            CompleteAlignmentTestScenario.RecognitionTuning,
            topK: 5,
            mapClass: scenario.Map.Class);

        Assert.Equal(GateSearchMode.FullSearch, scan.GateDetection.SearchModeUsed);
        Assert.Equal(GateSearchStopReason.Completed, scan.GateDetection.StopReason);
        Assert.NotEqual(
            GateSearchStopReason.DualGateEarlyExit,
            scan.GateDetection.StopReason);
        Assert.True(scan.GateDetection.ScalesEvaluated >= 8);
    }
}
