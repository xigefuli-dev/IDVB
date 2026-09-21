using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class SideEntranceStructuralScanBenchmarkTests
{
    [Fact]
    public async Task SideScan_CompletesWithin1000MsHardBoundary_AndSubsequentAlignmentSucceeds()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);

        var sw = Stopwatch.StartNew();
        var scan = scenario.Service.RunSideEntranceScan(
            frame,
            CompleteAlignmentTestScenario.RecognitionTuning,
            topK: 5,
            mapClass: scenario.Map.Class);
        sw.Stop();

        // 1. 硬性时间边界断言：整个扫描过程必须 <= 1000ms
        Assert.True(
            sw.ElapsedMilliseconds <= ScanExecutionPolicy.For(ScanPerformanceMode.Balanced).BudgetMilliseconds,
            $"侧门扫描耗时 {sw.ElapsedMilliseconds}ms 超过硬性上限 {ScanExecutionPolicy.For(ScanPerformanceMode.Balanced).BudgetMilliseconds:F0}ms");

        // 2. 候选产出断言
        Assert.True(
            scan.Candidates.Count > 0,
            $"Candidates count: {scan.Candidates.Count}, FailureReason: {scan.FailureReason}, Gates: {scan.GateDetection.Gates.Count}, ReadyMapCount: {scan.ReadyMapCount}, EligibleMapCount: {scan.EligibleMapCount}, Rejected: {scan.RejectedCandidateCount}");
        var bestCandidate = scan.Candidates[0];
        Assert.Equal(scenario.Map.Id, bestCandidate.Map.Id);
        Assert.Equal(CompleteAlignmentTestScenario.MainFloor, bestCandidate.FloorKey);
        Assert.True(
            bestCandidate.MatchScore >= .45,
            $"正常完整轮廓召回分意外下降为 {bestCandidate.MatchScore:P1}");
        Assert.Equal(1.0d, bestCandidate.MatchScale, 4);

        // 3. 对齐连带边界断言：扫描给出的候选结果，必须让随后的 VPSG3 对齐能通过
        var seedCreated = scenario.Service.TryCreateSideEntranceAlignmentSeed(
            bestCandidate,
            frame.ViewportBounds,
            out var seed,
            out var seedFailure);
        Assert.True(seedCreated, $"生成对齐种子失败: {seedFailure}");

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

        Assert.NotNull(attempt.Recognition);
        Assert.True(
            attempt.StructureAccepted,
            $"扫描产出的候选对齐未通过，扫描直接算失败。对齐失败原因: {attempt.StructureFailureReason}");
    }
}
