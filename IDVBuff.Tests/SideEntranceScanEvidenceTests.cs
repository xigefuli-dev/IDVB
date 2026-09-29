using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceScanPipelineTests
{
    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    public void OrdinaryEvidenceRemovesChestButPreservesThinPurpleWall(ScanPerformanceMode mode)
    {
        using var image = new Mat(240, 320, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(80, 100, 26, 16), new Scalar(195, 169, 172), -1);
        Cv2.Rectangle(image, new Rect(180, 100, 80, 40), new Scalar(110, 97, 88), -1);
        Cv2.Rectangle(image, new Rect(180, 100, 30, 3), new Scalar(195, 169, 172), -1);
        var viewport = new MapScreenRect(0, 0, 320, 240);
        using var original = Vpsg3FastLiveExtractor.Extract(image, viewport, 256);
        using var evidence = new ScanFrameEvidence(image, viewport, [], ScanExecutionPolicy.For(mode));
        using var chestBefore = new Mat(original.ObservedEdges, new Rect(76, 96, 34, 24));
        using var chestAfter = new Mat(evidence.Observation.ObservedEdges, new Rect(76, 96, 34, 24));
        Assert.True(Cv2.CountNonZero(chestBefore) > 0);
        Assert.Equal(0, Cv2.CountNonZero(chestAfter));
        using var wallBefore = new Mat(original.ObservedEdges, new Rect(175, 95, 40, 14));
        using var wallAfter = new Mat(evidence.Observation.ObservedEdges, new Rect(175, 95, 40, 14));
        Assert.True(Cv2.CountNonZero(wallBefore) > 0);
        Assert.Equal(0, Cv2.Norm(wallBefore, wallAfter, NormTypes.INF));
    }

    [Fact]
    public void UnrelatedAnchorCannotChangeSharedScanEvidence()
    {
        using var scene = new StructuralScanScene();
        // Small explored room next to the measured door. The old 53 * 5 + 18
        // envelope erased it even though the unrelated map was never selected.
        Cv2.Rectangle(scene.Frame, new Rect(420, 240, 60, 100), new Scalar(110, 97, 88), -1);
        Cv2.Rectangle(scene.Line, new Rect(420, 240, 60, 100), Scalar.White, 1);
        var other = scene.CreateMatchingMap();
        MapFloorRules.GetFloorProfile(other, "1f")!.FindAnchor("side-entrance")!.Bounds =
            new() { X = 373.5 / 800, Y = 273.5 / 600, Width = 53d / 800, Height = 53d / 600 };
        Point[] original;
        using (var first = ScanExecutionContext.Enter(ScanPerformanceMode.Quality))
        {
            first.CompleteAutomaticPhase();
            scene.Scan();
            original = first.Frame!.DensePoints.ToArray();
        }
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.Quality);
        context.CompleteAutomaticPhase();
        var found = new SideEntranceScanPipeline().RunScan(scene.Frame,
            [(scene.Map, "1f", scene.Line), (other, "1f", scene.Line)],
            [scene.Gate], viewportBounds: scene.Viewport);
        Assert.Equal(original, context.Frame!.DensePoints);
        Assert.Contains(context.Frame.DensePoints, p => p.X >= 450 && p.X <= 480 && p.Y >= 240 && p.Y <= 340);
        // Verify the production frame itself, not a newly extracted small-mask frame.
        var candidate = Assert.Single(found, c => c.Map.Id == scene.Map.Id);
        Assert.Contains(candidate.SearchHypotheses, proposal =>
            SideEntranceScanPipeline.TryCreateAlignmentSeed(proposal, scene.Viewport, out var seed, out _)
            && ScanIdentityVerifier.Verify(context.Frame, proposal.StructureIndex!,
                seed.LockedTransform, scene.Viewport, context).State == ScanIdentityState.Supported);
        Assert.False(ScanStructureIndex.Get(scene.Line).IsUnknown(400, 300));
        Assert.True(candidate.StructureIndex!.IsUnknown(400, 300));
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    public void ReferenceUnknownIsNeutralButCannotHideMissingWalls(ScanPerformanceMode mode)
    {
        using var image = new Mat(400, 600, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(50, 70, 320, 200), new Scalar(110, 97, 88), -1);
        Cv2.Rectangle(image, new Rect(440, 140, 40, 40), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 600, 400);
        using var context = ScanExecutionContext.Enter(mode);
        context.CompleteAutomaticPhase();
        using var frame = new ScanFrameEvidence(image, viewport, [], context.Policy);
        using var reference = frame.Observation.ObservedEdges.Clone();
        var anchor = new Rect(430, 130, 60, 60);
        Cv2.Rectangle(reference, anchor, Scalar.Black, -1);
        var index = ScanStructureIndex.Get(reference).WithUnknownBounds(anchor);
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        Assert.Equal(1, index.Score(frame.DensePoints, 1, 0, 0));
        var result = ScanIdentityVerifier.Verify(frame, index, transform, viewport, context);
        Assert.Equal(ScanIdentityState.Supported, result.State);
        Assert.True(result.UnknownReferencePoints > 80);
        using var wrong = reference.Clone();
        Cv2.Rectangle(wrong, new Rect(150, 60, 100, 30), Scalar.Black, -1);
        Assert.Equal(ScanIdentityState.Excluded, ScanIdentityVerifier.Verify(frame,
            ScanStructureIndex.Get(wrong).WithUnknownBounds(anchor), transform, viewport, context).State);
        var allUnknown = index.WithUnknownBounds(new(0, 0, 600, 400));
        Assert.Equal(0, allUnknown.Score(frame.DensePoints, 1, 0, 0));
        Assert.Equal(ScanIdentityState.Unverified,
            ScanIdentityVerifier.Verify(frame, allUnknown, transform, viewport, context).State);
        // A few early conflicting pixels do not prove an identity excluded if
        // most later pixels have no reference. Excluding it would allow false uniqueness.
        using var empty = new Mat(reference.Size(), MatType.CV_8UC1, Scalar.Black);
        var mostlyUnknown = ScanStructureIndex.Get(empty).WithUnknownBounds(new(0, 80, 600, 320));
        Assert.Equal(ScanIdentityState.Unverified,
            ScanIdentityVerifier.Verify(frame, mostlyUnknown, transform, viewport, context).State);
    }

}
