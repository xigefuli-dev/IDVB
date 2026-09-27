using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ScanObservationTests
{
    [Fact]
    public void FrameReuseRequiresExactPixelsAndSameCaptureGeometryAndResetsOnReopen()
    {
        var bounds = new MapScreenRect(100, 100, 800, 600);
        using var frame = new CapturedGameFrame(new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
            bounds, bounds, new IntPtr(1));
        using var cache = new ScanObservationFrameCache();
        cache.Remember(frame);
        Assert.True(cache.Matches(frame));
        using var changed = new CapturedGameFrame(frame.Image.Clone(), bounds, bounds, new IntPtr(1));
        changed.Image.Set(10, 10, new Vec3b(0, 0, 1));
        Assert.False(cache.Matches(changed));
        using var moved = new CapturedGameFrame(frame.Image.Clone(), bounds with { X = 101 }, bounds, new IntPtr(1));
        Assert.False(cache.Matches(moved));
        using var cropped = new CapturedGameFrame(frame.Image.Clone(), bounds, bounds with { Width = 799 }, new IntPtr(1));
        Assert.False(cache.Matches(cropped));
        using var otherWindow = new CapturedGameFrame(frame.Image.Clone(), bounds, bounds, new IntPtr(2));
        Assert.False(cache.Matches(otherWindow));
        cache.Reset();
        Assert.False(cache.Matches(frame));
    }

    [Fact]
    public void SmallExploredRegionCanReachVerificationButBlankFrameCannot()
    {
        using var image = new Mat(600, 800, MatType.CV_8UC3, Scalar.Black);
        Assert.False(ScanObservationRules.HasVisibleStructure(image));
        Cv2.Rectangle(image, new Rect(80, 130, 120, 90), new Scalar(110, 97, 88), -1);
        Assert.False(MapViewportPresenceDetector.Evaluate(image).IsPresent);
        Assert.True(ScanObservationRules.HasVisibleStructure(image));
    }

    [Fact]
    public void RepeatedAmbiguityOnlyKeepsPreviewWithoutPromotingIdentity()
    {
        var first = Candidate(.2);
        var second = Candidate(1.5);
        for (var i = 0; i < 100; i++)
        {
            Assert.Null(ScanIdentityVerifier.SelectIdentity([first, second], true, true));
            Assert.Same(second, ScanObservationRules.SelectPreview([first, second], second.Map.Id, second.FloorKey));
        }
        second.IdentityEvidence = second.IdentityEvidence with { State = ScanIdentityState.Excluded };
        Assert.Same(first, ScanObservationRules.SelectPreview([first, second], second.Map.Id, second.FloorKey));
        Assert.Equal(first.Map.Id, ScanIdentityVerifier.SelectIdentity([first, second], true, true));
    }

    [Fact]
    public void UnverifiedOrMissingAlignmentCannotReplaceResource()
    {
        var first = Candidate(.1);
        first.VerifiedTransform = null;
        var second = Candidate(.2);
        second.IdentityEvidence = ScanIdentityEvidence.Unverified("deadline");
        Assert.Null(ScanObservationRules.SelectPreview([first, second], first.Map.Id, first.FloorKey));
        Assert.Null(ScanIdentityVerifier.SelectIdentity([first, second], true, true));
    }

    [Fact]
    public void PreviousFloorDoesNotBiasAnotherFloorsSelection()
    {
        var first = Candidate(.2);
        var second = Candidate(2);
        Assert.Same(first, ScanObservationRules.SelectPreview([first, second], second.Map.Id, "2F"));
        Assert.Null(ScanObservationRules.SelectPreview([], second.Map.Id, "1F"));
    }

    [Fact]
    public void RevealingNewRoomResolvesVariantsWithoutPenalizingUnexploredReference()
    {
        // Same rendering/extractor used by production. The initial mask contains only a
        // shared room; revealing a distinct wall changes identity evidence, not frame count.
        using var partial = new Mat(600, 800, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(partial, new Rect(80, 130, 200, 140), new Scalar(110, 97, 88), -1);
        using var full = partial.Clone();
        Cv2.Rectangle(full, new Rect(420, 180, 220, 170), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 800, 600);
        using var initial = new ScanFrameEvidence(partial, viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.Balanced));
        using var revealed = new ScanFrameEvidence(full, viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.Balanced));
        using var firstLine = initial.Observation.ObservedEdges.Clone();
        using var secondLine = revealed.Observation.ObservedEdges.Clone();
        var first = Candidate(0);
        var second = Candidate(0);
        foreach (var observation in new[] { initial, revealed })
        {
            first.IdentityEvidence = ScanIdentityVerifier.Verify(observation, ScanStructureIndex.Get(firstLine),
                first.VerifiedTransform!, viewport, null);
            second.IdentityEvidence = ScanIdentityVerifier.Verify(observation, ScanStructureIndex.Get(secondLine),
                second.VerifiedTransform!, viewport, null);
            Assert.Equal(ScanIdentityState.Supported, second.IdentityEvidence.State);
            if (ReferenceEquals(observation, initial))
            {
                Assert.Equal(ScanIdentityState.Supported, first.IdentityEvidence.State);
                Assert.Null(ScanIdentityVerifier.SelectIdentity([first, second], true, true));
            }
            else
            {
                Assert.Equal(ScanIdentityState.Excluded, first.IdentityEvidence.State);
                Assert.Equal(second.Map.Id, ScanIdentityVerifier.SelectIdentity([first, second], true, true));
            }
        }
    }

    private static SideEntranceScanCandidate Candidate(double distance) => new()
    {
        Map = new MapRecord { Id = Guid.NewGuid() }, FloorKey = "1F",
        Disposition = SideEntranceCandidateDisposition.Reliable,
        VerifiedTransform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 },
        IdentityEvidence = new(ScanIdentityState.Supported, 100, 100, distance, .99, 0, "supported")
    };
}
