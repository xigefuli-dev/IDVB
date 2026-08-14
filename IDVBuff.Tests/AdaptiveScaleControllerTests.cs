using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleControllerTests
{
    [Fact]
    public void ProvisionalBecomesStableAfterThreeDistinctStructureFrames()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(1, 1.0, null, trusted: false);
        var start = DateTimeOffset.UtcNow;

        Assert.Null(controller.ObserveAbsolute(Observation(1, start, 1.0100)));
        Assert.Null(controller.ObserveAbsolute(Observation(2, start.AddMilliseconds(250), 1.0105)));
        var consensus = controller.ObserveAbsolute(
            Observation(3, start.AddMilliseconds(500), 1.0098));

        Assert.NotNull(consensus);
        controller.CommitConsensus(consensus!);
        Assert.Equal(AdaptiveScaleState.Stable, controller.State);
        Assert.True(controller.IsReliable);
        Assert.InRange(controller.RuntimeScale, 1.0098, 1.0105);
    }

    [Fact]
    public void SameFrameCannotVoteTwice()
    {
        var options = Options();
        var window = new AdaptiveScaleObservationWindow(options);
        var start = DateTimeOffset.UtcNow;

        Assert.True(window.Add(Observation(7, start, 1.0)));
        Assert.False(window.Add(Observation(7, start.AddMilliseconds(250), 1.0)));
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void SignificantDirectionReversalResetsConfirmationRun()
    {
        var window = new AdaptiveScaleObservationWindow(Options());
        var start = DateTimeOffset.UtcNow;
        window.Add(Observation(1, start, 1.0));
        window.Add(Observation(2, start.AddMilliseconds(250), 1.01));

        window.Add(Observation(3, start.AddMilliseconds(500), 1.0));

        Assert.Equal(1, window.Count);
        Assert.Null(window.TryGetConsensus());
    }

    [Fact]
    public void TwoStructureAndOneVpsgCanFastConfirm()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(1, 1.0, null, trusted: false);
        var start = DateTimeOffset.UtcNow;

        controller.ObserveAbsolute(Observation(1, start, 1.0100));
        controller.ObserveAbsolute(Observation(2, start.AddMilliseconds(250), 1.0110));
        var consensus = controller.ObserveAbsolute(Observation(
            3,
            start.AddMilliseconds(500),
            1.0105,
            AdaptiveScaleObservationSource.Vpsg,
            0.90));

        Assert.NotNull(consensus);
        Assert.Equal(1, consensus!.VpsgCount);
    }

    [Fact]
    public void SameFrameStructureAndVpsgAreIndependentAlgorithmVotes()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(1, 1.0, null, trusted: false);
        var start = DateTimeOffset.UtcNow;

        Assert.Null(controller.ObserveAbsolute(Observation(1, start, 1.0100)));
        Assert.Null(controller.ObserveAbsolute(Observation(
            1,
            start,
            1.0102,
            AdaptiveScaleObservationSource.Vpsg,
            0.90)));
        var consensus = controller.ObserveAbsolute(
            Observation(2, start.AddMilliseconds(250), 1.0101));

        Assert.NotNull(consensus);
        Assert.Equal(2, consensus!.StructureCount);
        Assert.Equal(1, consensus.VpsgCount);
    }

    [Fact]
    public void OrbScaleOnlyChallengesAndNeverChangesRuntimeScale()
    {
        var controller = CreateController("2f");
        controller.BeginOrResumeOpen(1, 1.1, 1.1, trusted: true);

        controller.ObserveOrbScale(1.006, DateTimeOffset.UtcNow);

        Assert.Equal(AdaptiveScaleState.Challenged, controller.State);
        Assert.Equal(1.1, controller.RuntimeScale, 8);
    }

    [Fact]
    public void ConfirmedChangeIsRuntimeZoomAndNotCalibration()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(1, 1.0, 1.0, trusted: true);
        var start = DateTimeOffset.UtcNow;
        controller.ObserveAbsolute(Observation(1, start, 1.02));
        controller.ObserveAbsolute(Observation(2, start.AddMilliseconds(250), 1.0205));
        var consensus = controller.ObserveAbsolute(
            Observation(3, start.AddMilliseconds(500), 1.0198));
        controller.CommitConsensus(consensus!);

        Assert.True(controller.HasRuntimeZoom);
        Assert.InRange(controller.RuntimeScale, 1.0198, 1.0205);
        Assert.Equal(1.0, controller.CalibrationScale);
    }

    [Fact]
    public void FloorKeysHaveIndependentState()
    {
        var first = CreateController("1f");
        var second = CreateController("2f");
        first.BeginOrResumeOpen(1, 1.0, null, trusted: false);
        second.BeginOrResumeOpen(1, 1.2, 1.2, trusted: true);

        first.ObserveOrbScale(1.006, DateTimeOffset.UtcNow);

        Assert.Equal(AdaptiveScaleState.Provisional, first.State);
        Assert.Equal(AdaptiveScaleState.Stable, second.State);
        Assert.Equal(1.2, second.RuntimeScale, 8);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void TwoStructureFailuresEnterRecovery()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(1, 1.0, 1.0, trusted: true);

        controller.ObserveStructureFailure();
        Assert.Equal(AdaptiveScaleState.Stable, controller.State);
        controller.ObserveStructureFailure();

        Assert.Equal(AdaptiveScaleState.Recovering, controller.State);
        Assert.False(controller.CanUseReliableScale(1.0));
    }

    [Fact]
    public void ConsensusRequiresExplicitValidatedCommit()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(1, 1.0, null, trusted: false);
        var start = DateTimeOffset.UtcNow;
        controller.ObserveAbsolute(Observation(1, start, 1.01));
        controller.ObserveAbsolute(Observation(2, start.AddMilliseconds(250), 1.0104));
        var consensus = controller.ObserveAbsolute(
            Observation(3, start.AddMilliseconds(500), 1.0102));

        Assert.Equal(1.0, controller.RuntimeScale, 8);

        controller.CommitConsensus(consensus!);

        Assert.Equal(consensus!.Scale, controller.RuntimeScale, 8);
        Assert.True(controller.IsReliable);
    }

    [Fact]
    public void RuntimeZoomResumesWithinOpenAndIsDiscardedAfterClose()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(11, 1.0, 1.0, trusted: true);
        var start = DateTimeOffset.UtcNow;
        controller.ObserveAbsolute(Observation(1, start, 1.02));
        controller.ObserveAbsolute(Observation(2, start.AddMilliseconds(250), 1.0202));
        var consensus = controller.ObserveAbsolute(
            Observation(3, start.AddMilliseconds(500), 1.0201));
        controller.CommitConsensus(consensus!);

        Assert.True(controller.HasRuntimeZoom);
        Assert.True(controller.BeginOrResumeOpen(11, 0.95, 1.0, trusted: false));
        Assert.InRange(controller.RuntimeScale, 1.02, 1.0202);

        controller.EndOpen(11);
        Assert.False(controller.IsOpen);
        Assert.False(controller.HasRuntimeZoom);
        Assert.False(controller.BeginOrResumeOpen(12, 1.0, 1.0, trusted: true));
        Assert.Equal(1.0, controller.RuntimeScale, 8);
    }

    [Fact]
    public void ExplicitEndAllowsFreshAlignmentEvenWhenToggleIdIsUnchanged()
    {
        var controller = CreateController("1f");
        controller.BeginOrResumeOpen(17, 1.0, 1.0, trusted: true);

        controller.EndOpen(17);
        var resumed = controller.BeginOrResumeOpen(
            17,
            1.2,
            calibrationScale: null,
            trusted: false);

        Assert.False(resumed);
        Assert.Equal(1.2, controller.RuntimeScale, 8);
        Assert.False(controller.HasReliableBaseline);
        Assert.Equal(AdaptiveScaleState.Provisional, controller.State);
    }

    private static AdaptiveScaleController CreateController(string floor) =>
        new(
            new AdaptiveScaleKey(Guid.NewGuid(), 10, floor, 1920, 1080, 1314, 1055),
            Options());

    private static AdaptiveScaleOptions Options() => new();

    private static AdaptiveScaleObservation Observation(
        long frame,
        DateTimeOffset at,
        double scale,
        AdaptiveScaleObservationSource source = AdaptiveScaleObservationSource.Structure,
        double confidence = 0.90) =>
        new(frame, at, scale, confidence, 0.10, source, Transform(scale));

    private static MapOverlayTransform Transform(double scale) => new()
    {
        ScaleX = scale,
        ScaleY = scale,
        ReferenceCenterX = 500,
        ReferenceCenterY = 400,
        ScreenCenterX = 600,
        ScreenCenterY = 500,
        ReferenceWidth = 1000,
        ReferenceHeight = 800,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };
}
