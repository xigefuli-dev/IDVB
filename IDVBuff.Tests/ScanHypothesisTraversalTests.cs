using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "7")]
public sealed class ScanHypothesisTraversalTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void SupportedPoseGetsAlignmentBeforeHigherRecallUnverifiedPose(ScanPerformanceMode mode)
    {
        using var scan = ScanExecutionContext.Enter(mode);
        var missing = Pose(ScanIdentityState.Unverified, double.PositiveInfinity, .91);
        var supported = Pose(ScanIdentityState.Supported, .3, .61);
        foreach (var hypotheses in new[] { new[] { missing, supported }, new[] { supported, missing } })
        {
            var visited = new List<SideEntranceScanCandidate>();
            Assert.True(ScanIdentityVerifier.TryConfirmHypotheses(hypotheses, scan, h =>
            {
                visited.Add(h);
                return h.IdentityEvidence.State == ScanIdentityState.Supported;
            }));
            Assert.Equal(new[] { supported }, visited);
        }
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void LocalFinalVerificationFailureStillAllowsNextPose(ScanPerformanceMode mode)
    {
        using var scan = ScanExecutionContext.Enter(mode);
        var first = Pose(ScanIdentityState.Supported, .1, .91);
        var next = Pose(ScanIdentityState.Supported, .3, .61);
        var visited = new List<SideEntranceScanCandidate>();
        Assert.True(ScanIdentityVerifier.TryConfirmHypotheses([first, next], scan, h =>
        {
            visited.Add(h);
            return ReferenceEquals(h, next); // first's formal transform had insufficient evidence
        }));
        Assert.Equal(new[] { first, next }, visited);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("superseded")]
    [InlineData("deadline")]
    public void GlobalStopAfterLocalFailureDoesNotVisitAnotherPose(string stop)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualClock();
        var current = true;
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Fast, cancellation.Token,
            () => current, timeProvider: clock);
        var visited = 0;
        Assert.False(ScanIdentityVerifier.TryConfirmHypotheses(
            [Pose(ScanIdentityState.Supported, .1, .91), Pose(ScanIdentityState.Supported, .3, .61)], scan, _ =>
            {
                visited++;
                if (stop == "cancelled") cancellation.Cancel();
                else if (stop == "superseded") current = false;
                else clock.Timestamp = TimeSpan.FromSeconds(1).Ticks;
                return false;
            }));
        Assert.Equal(1, visited);
        Assert.False(scan.CanCompute);
    }

    [Fact]
    public void ReadyCatalogCannotErasePreviouslyIncompleteRetrieval()
    {
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced);
        scan.RecordRetrievalCoverage(3, 2);
        Assert.Equal(3, scan.EligibleIdentities);
        Assert.False(scan.RetrievalCompleted);
        scan.RecordRetrievalCoverage(3, 3);
        Assert.False(scan.RetrievalCompleted);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("superseded")]
    [InlineData("deadline")]
    public void ConfirmationCannotSucceedAfterLosingExecutionContext(string stop)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualClock();
        var current = true;
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Fast, cancellation.Token,
            () => current, timeProvider: clock);
        Assert.False(ScanIdentityVerifier.TryConfirmHypotheses(
            [Pose(ScanIdentityState.Supported, .1, .91)], scan, _ =>
            {
                if (stop == "cancelled") cancellation.Cancel();
                else if (stop == "superseded") current = false;
                else clock.Timestamp = TimeSpan.FromSeconds(1).Ticks;
                return true;
            }));
    }

    private static SideEntranceScanCandidate Pose(ScanIdentityState state, double distance, double score) => new()
    {
        MatchScore = score,
        IdentityEvidence = new(state, 100, 100, distance, .95, 0, "regression")
    };

    private sealed class ManualClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
