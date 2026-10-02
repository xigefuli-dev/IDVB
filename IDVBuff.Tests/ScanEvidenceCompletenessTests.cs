using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ScanEvidenceCompletenessTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast, false)]
    [InlineData(ScanPerformanceMode.Balanced, false)]
    [InlineData(ScanPerformanceMode.Fast, true)]
    [InlineData(ScanPerformanceMode.Balanced, true)]
    public void NearFitEligibilityDoesNotDependOnWhereConflictingPointsAreVisited(
        ScanPerformanceMode mode, bool hasUnknownReference)
    {
        using var image = new Mat(400, 600, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(60, 80, 360, 220), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, image.Width, image.Height);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var reference = frame.Observation.ObservedEdges.Clone();
        Cv2.Rectangle(reference, new Rect(90, 65, 180, 30), Scalar.Black, -1);
        var index = ScanStructureIndex.Get(reference);
        if (hasUnknownReference) index = index.WithUnknownBounds(new Rect(250, 285, 90, 30));
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        ScanIdentityEvidence full;
        using (var quality = ScanExecutionContext.Enter(ScanPerformanceMode.Quality))
        {
            quality.CompleteAutomaticPhase();
            full = ScanIdentityVerifier.Verify(frame, index, transform, viewport, quality);
        }
        Assert.InRange(full.SupportedFraction, .80, .879999);
        Assert.Equal(ScanIdentityState.Excluded, full.State);
        Assert.Equal("unexplained-visible-structure", full.Reason);
        Assert.False(ScanIdentityVerifier.HasRefinablePose(full with { TestedPoints = full.TestedPoints - 1 }));

        using var context = ScanExecutionContext.Enter(mode);
        context.CompleteAutomaticPhase();
        // Keep exactly the same observed pixels and pose; only reverse visitation.
        // Both near fits must reach correction, while neither may confirm identity.
        foreach (var reverse in new[] { false, true })
        {
            if (reverse) Array.Reverse(frame.DensePoints);
            var evidence = ScanIdentityVerifier.Verify(frame, index, transform, viewport, context);
            Assert.Equal(full.SupportedFraction, evidence.SupportedFraction, 10);
            Assert.Equal(full.ForwardMeanPixels, evidence.ForwardMeanPixels, 10);
            Assert.Equal(evidence.TotalPoints, evidence.TestedPoints + evidence.UnknownReferencePoints);
            Assert.True(ScanIdentityVerifier.HasRefinablePose(evidence));
            Assert.Equal(ScanIdentityState.Excluded, evidence.State);
        }
    }
}
