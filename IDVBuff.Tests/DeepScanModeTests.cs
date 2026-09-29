using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class DeepScanModeTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.DeepScan, true)]
    [InlineData(ScanPerformanceMode.Fast, false)]
    [InlineData(ScanPerformanceMode.Balanced, false)]
    [InlineData(ScanPerformanceMode.Quality, false)]
    public void ReferenceChoicesCanBeShownOnlyByDeepScanWithoutBecomingAutomaticIdentity(
        ScanPerformanceMode mode, bool expected)
    {
        using var execution = ScanExecutionContext.Enter(mode);
        var candidate = new SideEntranceScanCandidate
        {
            Map = new() { Id = Guid.NewGuid() }, FloorKey = "1f",
            IdentityEvidence = new(ScanIdentityState.Excluded, 100, 100, 12, .6, 30, "spatial-support-conflict")
        };
        MapRecognitionChoice[] choices = [new() { IsReferenceOnly = true }];
        Assert.Equal(expected, MapCandidatePresentationRules.CanPresentChoices(execution, choices, [candidate]));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([candidate], true, true));
        candidate.IdentityEvidence = ScanIdentityEvidence.Unverified("alignment-not-confirmed");
        Assert.Equal(expected, MapCandidatePresentationRules.CanPresentChoices(execution, choices, [candidate]));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([candidate], true, true));
    }

    [Fact]
    public void DeepScanCannotPresentCancelledOrIncompleteResults()
    {
        MapRecognitionChoice[] choices = [new() { IsReferenceOnly = true }];
        using var cancellation = new CancellationTokenSource();
        using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan, cancellation.Token);
        execution.RetrievalCompleted = false;
        Assert.False(MapCandidatePresentationRules.CanPresentChoices(execution, choices, []));
        execution.RetrievalCompleted = true;
        cancellation.Cancel();
        Assert.False(MapCandidatePresentationRules.CanPresentChoices(execution, choices, []));
    }

    [Fact]
    public void ExpiredDeepScanCanOfferCompletedChoicesButCannotConfirmAutomatically()
    {
        using var execution = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan,
            startedTimestamp: System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 3);
        Assert.True(execution.Expired);
        Assert.True(MapCandidatePresentationRules.CanPresentChoices(execution, [new() { IsReferenceOnly = true }], []));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([], true, execution.CanCompute));
    }

    [Theory]
    [InlineData(ScanPerformanceMode.DeepScan, true)]
    [InlineData(ScanPerformanceMode.Balanced, false)]
    public void AmbiguousLocalIdentitiesWaitForSelectionBeforeAlignment(ScanPerformanceMode mode, bool expected)
    {
        using var execution = ScanExecutionContext.Enter(mode);
        var candidates = Enumerable.Range(0, 2).Select(_ => new SideEntranceScanCandidate
        {
            Map = new() { Id = Guid.NewGuid() }, FloorKey = "1f",
            IdentityEvidence = new(ScanIdentityState.Supported, 100, 100, .2, 1, 0, "visible-structure-supported")
        }).ToArray();
        Assert.Equal(expected, ScanUncertainPolicies.DeferAmbiguousDeepScanAlignment(execution, candidates));
        execution.VariantGroups = [candidates.Select(c => c.Map.Id).ToArray()];
        Assert.Equal(expected, ScanUncertainPolicies.DeferAmbiguousDeepScanAlignment(execution, candidates));
        Assert.False(ScanUncertainPolicies.DeferAmbiguousDeepScanAlignment(execution, candidates.Take(1).ToArray()));
    }

    [Fact]
    public void DedicatedPolicyHasTwoSecondBudgetWithoutChangingOtherModes()
    {
        var quality = ScanExecutionPolicy.For(ScanPerformanceMode.Quality);
        var deep = ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan);
        Assert.Equal(ScanPerformanceMode.DeepScan, deep.Mode);
        Assert.Equal(2000, deep.BudgetMilliseconds);
        Assert.Equal(1000, quality.BudgetMilliseconds);
        Assert.Equal(1000, ScanExecutionPolicy.For(ScanPerformanceMode.Balanced).BudgetMilliseconds);
        Assert.Equal(500, ScanExecutionPolicy.For(ScanPerformanceMode.Fast).BudgetMilliseconds);
        Assert.NotEqual(quality, deep with { Mode = ScanPerformanceMode.Quality });
    }

    [Theory]
    [InlineData("3")]
    [InlineData("\"DeepScan\"")]
    public void SavedModeSurvivesReadNormalizeCloneAndWrite(string value)
    {
        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>("{\"ScanPerformanceMode\":" + value + "}")!;
        settings.Normalize();
        var copy = settings.Clone();
        Assert.Equal(ScanPerformanceMode.DeepScan, copy.ScanPerformanceMode);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(JsonSerializer.Serialize(copy))!;
        Assert.Equal(ScanPerformanceMode.DeepScan, restored.ScanPerformanceMode);
        Assert.Equal(0, (int)ScanPerformanceMode.Fast);
        Assert.Equal(1, (int)ScanPerformanceMode.Balanced);
        Assert.Equal(2, (int)ScanPerformanceMode.Quality);
    }

    [Fact]
    public void SwitchingModeDoesNotMutateInFlightScanOrReuseQualityFrame()
    {
        var bounds = new MapScreenRect(0, 0, 20, 20);
        using var frame = new CapturedGameFrame(new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
            bounds, bounds, new IntPtr(1));
        using var cache = new ScanObservationFrameCache();
        cache.Remember(frame, "catalog", ScanPerformanceMode.Quality);
        Assert.False(cache.Matches(frame, "catalog", ScanPerformanceMode.DeepScan));
        using var current = ScanExecutionContext.Enter(ScanPerformanceMode.Quality);
        using (var next = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan))
        {
            Assert.Equal(ScanPerformanceMode.DeepScan, next.Policy.Mode);
            Assert.Equal(ScanPerformanceMode.Quality, current.Policy.Mode);
        }
        Assert.Same(current, ScanExecutionContext.Current);
    }

    [Fact]
    public void DeepScanUsesQualityFullEvidenceVerificationRatherThanEarlyExit()
    {
        using var image = new Mat(600, 800, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(80, 130, 200, 140), new Scalar(110, 97, 88), -1);
        Cv2.Rectangle(image, new Rect(390, 120, 250, 200), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 800, 600);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan));
        using var reference = frame.Observation.ObservedEdges.Clone();
        Cv2.Rectangle(reference, new Rect(0, 0, 340, 600), Scalar.Black, -1);
        var index = ScanStructureIndex.Get(reference);
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        ScanIdentityEvidence Verify(ScanPerformanceMode mode)
        {
            using var context = ScanExecutionContext.Enter(mode);
            return ScanIdentityVerifier.Verify(frame, index, transform, viewport, context);
        }
        var quality = Verify(ScanPerformanceMode.Quality);
        var deep = Verify(ScanPerformanceMode.DeepScan);
        Assert.Equal(ScanIdentityState.Excluded, deep.State);
        Assert.Equal(quality, deep);
    }
}
