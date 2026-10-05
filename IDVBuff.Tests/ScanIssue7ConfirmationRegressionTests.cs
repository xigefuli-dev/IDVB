using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "7")]
public sealed class ScanIssue7ConfirmationRegressionTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void FailedFinalGeometryDoesNotPreventAnotherPoseFromConfirmingTheSameMap(ScanPerformanceMode mode)
    {
        using var image = VisibleStructure();
        var viewport = new MapScreenRect(0, 0, image.Width, image.Height);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var reference = frame.Observation.ObservedEdges.Clone();
        var index = ScanStructureIndex.Get(reference);
        using var scan = ScanExecutionContext.Enter(mode, timeProvider: new ManualClock());
        scan.RecordRetrievalCoverage(1, 1);
        var map = new MapRecord { Id = Guid.NewGuid() };
        var initialEvidence = ScanIdentityVerifier.Verify(frame, index, Transform(0), viewport, scan);
        Assert.Equal(ScanIdentityState.Supported, initialEvidence.State);
        var failedPose = Pose(map, initialEvidence, .99);
        var successfulPose = Pose(map, initialEvidence, .80);
        var aggregate = Pose(map, initialEvidence, .99);
        var visited = new List<SideEntranceScanCandidate>();
        ScanIdentityEvidence? failedFinalEvidence = null;
        ScanIdentityEvidence? bestEvidence = null;
        MapRecognitionAttempt? bestAttempt = null;

        var confirmed = ScanIdentityVerifier.TryConfirmHypotheses([successfulPose, failedPose], scan, pose =>
        {
            visited.Add(pose);
            var transform = Transform(ReferenceEquals(pose, failedPose) ? 65 : 0);
            var finalEvidence = ScanIdentityVerifier.Verify(frame, index, transform, viewport, scan);
            if (finalEvidence.State != ScanIdentityState.Supported)
            {
                failedFinalEvidence = finalEvidence;
                return false;
            }
            pose.VerifiedTransform = transform;
            pose.IdentityEvidence = bestEvidence = finalEvidence;
            bestAttempt = Attempt(map, transform);
            return true;
        });

        Assert.True(confirmed);
        Assert.True(SideEntranceCandidateEvidence.ApplyConfirmedScanAttempt(
            aggregate, confirmed, bestEvidence, bestAttempt));
        Assert.Equal(new[] { failedPose, successfulPose }, visited);
        Assert.Equal(ScanIdentityState.Excluded, failedFinalEvidence!.State);
        Assert.Same(initialEvidence, failedPose.IdentityEvidence);
        Assert.Null(failedPose.VerifiedTransform);
        Assert.Equal(SideEntranceCandidateDisposition.Reliable, aggregate.Disposition);
        Assert.Same(bestAttempt!.Recognition!.Result.OverlayTransform, aggregate.VerifiedTransform);
        Assert.Equal(0, aggregate.VerifiedTransform!.OffsetX);
        Assert.Same(bestEvidence, aggregate.IdentityEvidence);
        var decision = ScanIdentityVerifier.EvaluateSelection([aggregate],
            scan.RetrievalCompleted, scan.CanCompute);
        Assert.Equal(map.Id, decision.MapId);
        Assert.Equal("selected", decision.Reason);
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void ConfirmedGeometryCannotEraseAnotherMapsUnknownReferenceEvidence(ScanPerformanceMode mode)
    {
        using var image = VisibleStructure();
        var viewport = new MapScreenRect(0, 0, image.Width, image.Height);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var reference = frame.Observation.ObservedEdges.Clone();
        var index = ScanStructureIndex.Get(reference);
        using var scan = ScanExecutionContext.Enter(mode, timeProvider: new ManualClock());
        scan.RecordRetrievalCoverage(2, 2);
        var known = Pose(new MapRecord { Id = Guid.NewGuid() },
            ScanIdentityVerifier.Verify(frame, index, Transform(0), viewport, scan), .8);
        var unknown = Pose(new MapRecord { Id = Guid.NewGuid() },
            ScanIdentityVerifier.Verify(frame,
                index.WithUnknownBounds(new Rect(0, 0, image.Width, image.Height)),
                Transform(0), viewport, scan), .99);
        Assert.Equal(ScanIdentityState.Supported, known.IdentityEvidence.State);
        Assert.Equal(ScanIdentityState.Unverified, unknown.IdentityEvidence.State);
        Assert.Equal(frame.DensePoints.Length, unknown.IdentityEvidence.UnknownReferencePoints);
        Assert.True(ScanIdentityVerifier.TryConfirmHypotheses([known], scan, pose =>
        {
            pose.VerifiedTransform = Transform(0);
            return true;
        }));
        Assert.True(SideEntranceCandidateEvidence.ApplyConfirmedScanAttempt(known,
            true, known.IdentityEvidence, Attempt(known.Map, known.VerifiedTransform!)));

        foreach (var policy in Enum.GetValues<ScanIdentitySelectionPolicy>())
        {
            var decision = ScanIdentityVerifier.EvaluateSelection([known, unknown],
                scan.RetrievalCompleted, scan.CanCompute, selectionPolicy: policy);
            Assert.Null(decision.MapId);
            Assert.Equal("unverified-identities", decision.Reason);
        }
        Assert.Equal(ScanIdentityState.Unverified, unknown.IdentityEvidence.State);
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void LateAcceptedTransformCannotPromoteAnOperationThatLostItsCommitBudget(ScanPerformanceMode mode)
    {
        var clock = new ManualClock();
        using var scan = ScanExecutionContext.Enter(mode, timeProvider: clock);
        var candidate = Pose(new MapRecord { Id = Guid.NewGuid() },
            new(ScanIdentityState.Supported, 100, 100, .1, .99, 0, "supported"), .9);
        var acceptedAttempt = Attempt(candidate.Map, Transform(12));
        var confirmed = ScanIdentityVerifier.TryConfirmHypotheses([candidate], scan, _ =>
        {
            clock.Timestamp = TimeSpan.FromMilliseconds(scan.Policy.BudgetMilliseconds - 60).Ticks;
            return true;
        });
        Assert.False(SideEntranceCandidateEvidence.ApplyConfirmedScanAttempt(candidate,
            confirmed, candidate.IdentityEvidence, acceptedAttempt));
        Assert.Equal(SideEntranceCandidateDisposition.NeedsVerification, candidate.Disposition);
        Assert.Null(candidate.VerifiedTransform);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([candidate], true, scan.CanCompute));
    }

    private static MapRecognitionAttempt Attempt(MapRecord map, MapOverlayTransform transform) => new()
    {
        StructureAccepted = true,
        Recognition = new() { Map = map, Result = new MapRecognitionResult
            { MapId = map.Id, Floor = "1f", OverlayTransform = transform } }
    };

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void TryingAnotherPoseKeepsTheOriginalCommitReserveAndDeadline(ScanPerformanceMode mode)
    {
        var clock = new ManualClock();
        using var scan = ScanExecutionContext.Enter(mode, timeProvider: clock);
        var map = new MapRecord { Id = Guid.NewGuid() };
        var evidence = new ScanIdentityEvidence(ScanIdentityState.Supported, 100, 100, .1, .99, 0, "supported");
        var visited = 0;
        Assert.False(ScanIdentityVerifier.TryConfirmHypotheses(
            [Pose(map, evidence, .99), Pose(map, evidence, .80)], scan, _ =>
            {
                visited++;
                clock.Timestamp = TimeSpan.FromMilliseconds(scan.Policy.BudgetMilliseconds - 60).Ticks;
                return false;
            }));
        Assert.Equal(1, visited);
        Assert.Equal(60, scan.RemainingMilliseconds);
        Assert.False(scan.CanCompute);
        Assert.Equal("commit-budget-reserved", scan.ComputeStopReason);
    }

    private static SideEntranceScanCandidate Pose(MapRecord map, ScanIdentityEvidence evidence, double recall) =>
        new() { Map = map, FloorKey = "1f", IdentityEvidence = evidence, MatchScore = recall };

    private static MapOverlayTransform Transform(double x) =>
        new() { ScaleX = 1, ScaleY = 1, OffsetX = x };

    private static Mat VisibleStructure()
    {
        var image = new Mat(400, 600, MatType.CV_8UC3, new Scalar(30, 25, 22));
        foreach (var room in new[] { new Rect(60, 50, 180, 130), new Rect(320, 80, 180, 200),
            new Rect(80, 240, 160, 100) })
            Cv2.Rectangle(image, room, new Scalar(110, 97, 88), -1);
        return image;
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
