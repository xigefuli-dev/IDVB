using System.Reflection;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
[Trait("Category", "IssueRegression")]
[Trait("Issue", "7")]
public sealed class ScanCatalogReadinessTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public async Task RestoringStructureCacheOnlyMakesTheNextScanComplete(ScanPerformanceMode mode)
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var cache = (Dictionary<(Guid, string), Mat>)typeof(MapCvRecognitionService)
            .GetField("_sideEntranceFeatureCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Service)!;
        var saved = cache.ToArray();
        Assert.NotEmpty(saved);
        cache.Clear();
        try
        {
            using var incomplete = ScanExecutionContext.Enter(mode, timeProvider: new FrozenClock());
            var missing = scenario.Service.RunSideEntranceScan(frame,
                CompleteAlignmentTestScenario.RecognitionTuning, mapClass: scenario.Map.Class);
            Assert.Equal(1, missing.EligibleMapCount);
            Assert.Equal(0, missing.ReadyMapCount);
            Assert.False(incomplete.RetrievalCompleted);
            foreach (var item in saved) cache.Add(item.Key, item.Value);

            using (var recovered = ScanExecutionContext.Enter(mode, timeProvider: new FrozenClock()))
            {
                var ready = scenario.Service.RunSideEntranceScan(frame,
                    CompleteAlignmentTestScenario.RecognitionTuning, mapClass: scenario.Map.Class);
                Assert.Equal(1, ready.EligibleMapCount);
                Assert.Equal(1, ready.ReadyMapCount);
                Assert.Equal(1, recovered.EligibleIdentities);
                Assert.True(recovered.RetrievalCompleted);
                Assert.DoesNotContain("就绪 0/1", ready.FailureReason);
            }
            Assert.Same(incomplete, ScanExecutionContext.Current);
            Assert.False(incomplete.RetrievalCompleted);
        }
        finally
        {
            foreach (var item in saved) cache.TryAdd(item.Key, item.Value);
        }
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public async Task MissingStructureCacheKeepsCatalogIdentityEligibleAndReportsIncomplete(ScanPerformanceMode mode)
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync(nativeStructure: true);
        using var frame = scenario.MainFrame(VisibleGates.SideOnly);
        var cache = (Dictionary<(Guid, string), Mat>)typeof(MapCvRecognitionService)
            .GetField("_sideEntranceFeatureCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scenario.Service)!;
        var saved = cache.ToArray();
        cache.Clear();
        try
        {
            using var scan = ScanExecutionContext.Enter(mode);
            var result = scenario.Service.RunSideEntranceScan(frame,
                CompleteAlignmentTestScenario.RecognitionTuning, mapClass: scenario.Map.Class);
            Assert.Equal(1, result.EligibleMapCount);
            Assert.Equal(0, result.ReadyMapCount);
            Assert.Equal(1, scan.EligibleIdentities);
            Assert.False(scan.RetrievalCompleted);
            Assert.Contains("就绪 0/1", result.FailureReason);
            Assert.Null(ScanIdentityVerifier.SelectIdentity(result.Candidates,
                scan.RetrievalCompleted, scan.CanCompute));
            Assert.False(MapCandidatePresentationRules.CanPresentChoices(scan,
                [new MapRecognitionChoice { IsReferenceOnly = true }]));
        }
        finally
        {
            foreach (var item in saved) cache.Add(item.Key, item.Value);
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }
}
