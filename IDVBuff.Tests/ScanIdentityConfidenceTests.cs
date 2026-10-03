using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ScanIdentityConfidenceTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    [InlineData(ScanPerformanceMode.DeepScan)]
    public void WeakLocalDisagreementDoesNotHaveStrongWallVetoAuthority(ScanPerformanceMode mode)
    {
        using var image = new Mat(600, 800, MatType.CV_8UC3, new Scalar(78, 70, 63));
        Cv2.Rectangle(image, new Rect(80, 130, 530, 330), new Scalar(170, 148, 135), -1);
        Cv2.Rectangle(image, new Rect(680, 380, 40, 40), new Scalar(106, 94, 84), -1);
        var viewport = new MapScreenRect(0, 0, 800, 600);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var weakRegion = new Mat(frame.Observation.ProposalEdges, new Rect(665, 365, 75, 75));
        Assert.Equal(0, Cv2.CountNonZero(weakRegion));
        using var reference = frame.Observation.ObservedEdges.Clone();
        Cv2.Rectangle(reference, new Rect(665, 365, 75, 75), Scalar.Black, -1);
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        var result = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(reference), transform, viewport, null);
        Assert.Equal(ScanIdentityState.Supported, result.State);
        Assert.True(result.StrongTestedPoints < result.TestedPoints);
        Assert.True(result.SupportedFraction < 1); // Weak misses still count globally.
        var exact = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(frame.Observation.ObservedEdges), transform, viewport, null);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([
            new SideEntranceScanCandidate { Map = new() { Id = Guid.NewGuid() }, IdentityEvidence = exact,
                Disposition = SideEntranceCandidateDisposition.Reliable },
            new SideEntranceScanCandidate { Map = new() { Id = Guid.NewGuid() }, IdentityEvidence = result,
                Disposition = SideEntranceCandidateDisposition.Reliable }
        ], true, true)); // A weak-only distinction does not prove a unique identity.
        // A missing strong wall remains a contradiction at the same global fit.
        using var wrong = reference.Clone();
        Cv2.Rectangle(wrong, new Rect(180, 120, 80, 25), Scalar.Black, -1);
        var rejected = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(wrong), transform, viewport, null);
        Assert.Equal(ScanIdentityState.Excluded, rejected.State);
        Assert.True(rejected.LongestConflictPixels >= ScanIdentityVerifier.MaximumContinuousConflictPixels);
    }

    [Fact]
    public void OnlyWeakStructureCannotEstablishAutomaticIdentity()
    {
        using var image = new Mat(240, 320, MatType.CV_8UC3, new Scalar(78, 70, 63));
        Cv2.Rectangle(image, new Rect(70, 70, 160, 100), new Scalar(106, 94, 84), -1);
        var viewport = new MapScreenRect(0, 0, 320, 240);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.Quality));
        Assert.True(frame.DensePoints.Length > 80);
        var evidence = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(frame.Observation.ObservedEdges),
            new MapOverlayTransform { ScaleX = 1, ScaleY = 1 }, viewport, null);
        Assert.Equal(ScanIdentityState.Unverified, evidence.State);
        Assert.Equal("insufficient-strong-visible-structure", evidence.Reason);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void ErasedReferenceSupportDiskUsesLivePixelTolerance(double scale)
    {
        using var line = new Mat(200, 300, MatType.CV_8UC1, Scalar.Black);
        var index = ScanStructureIndex.Get(line).WithUnknownBounds(new Rect(100, 80, 20, 30));
        var radius = ScanIdentityVerifier.SupportTolerancePixels / scale;
        Assert.True(index.IsUnknownWithinSupport(100 - radius, 90, scale));
        Assert.False(index.IsUnknownWithinSupport(100 - radius - .01, 90, scale));
        Assert.True(index.IsUnknownWithinSupport(119 + radius, 90, scale));
        Assert.False(index.IsUnknownWithinSupport(119 + radius + .01, 90, scale));
        // Use a disk, not the larger bounding square, near erased corners.
        Assert.False(index.IsUnknownWithinSupport(100 - radius, 80 - radius, scale));
        Assert.False(ScanStructureIndex.Get(line).IsUnknownWithinSupport(100, 90, scale));
    }

    [Fact]
    public void SubpixelProjectionOutsideErasedWallDoesNotInventConflict()
    {
        using var image = new Mat(400, 600, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(80, 70, 350, 230), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 600, 400);
        using var frame = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.Quality));
        using var reference = frame.Observation.ObservedEdges.Clone();
        var anchor = new Rect(78, 110, 8, 130);
        Cv2.Rectangle(reference, anchor, Scalar.Black, -1);
        var index = ScanStructureIndex.Get(reference).WithUnknownBounds(anchor);
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1, OffsetX = 2.25 };
        var supported = ScanIdentityVerifier.Verify(frame, index, transform, viewport, null);
        Assert.Equal(ScanIdentityState.Supported, supported.State);
        Assert.True(supported.UnknownReferencePoints > 80);
        // The same missing line with no erasure provenance must still reject.
        var rejected = ScanIdentityVerifier.Verify(frame, ScanStructureIndex.Get(reference), transform, viewport, null);
        Assert.Equal(ScanIdentityState.Excluded, rejected.State);
    }
}
