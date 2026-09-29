using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class DeepScanPipelineTests
{
    [Fact]
    public void DeepDistanceLookupIsExactlyEquivalentIncludingBoundsAndScale()
    {
        using var line = new Mat(200, 300, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(line, new(30, 20), new(270, 180), Scalar.White, 3);
        var index = ScanStructureIndex.Get(line);
        var random = new Random(42);
        for (var i = 0; i < 10000; i++)
        {
            var x = random.NextDouble() * 320 - 10;
            var y = random.NextDouble() * 220 - 10;
            var scale = .1 + random.NextDouble() * 4.9;
            Assert.Equal(index.Distance(x,y,scale), index.DeepScanDistance(x,y,scale));
        }
        Assert.Equal(index.Distance(double.NaN,0,1),index.DeepScanDistance(double.NaN,0,1));
    }

    [Fact]
    public void PreparedConflictGeometryKeepsTheSameEvidenceAsQualityVerification()
    {
        using var scene = new Scene(1.6);
        using var frame = new ScanFrameEvidence(scene.Frame, scene.Viewport, [], ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan));
        var index = ScanStructureIndex.Get(scene.Line);
        foreach (var delta in new[] { 0, 3, 12, 35 })
        {
            var transform = new MapOverlayTransform { ScaleX = 1.6, ScaleY = 1.6,
                OffsetX = scene.X + scene.Viewport.X + delta, OffsetY = scene.Y + scene.Viewport.Y };
            ScanIdentityEvidence ordinary;
            using (var quality = ScanExecutionContext.Enter(ScanPerformanceMode.Quality))
                ordinary = ScanIdentityVerifier.Verify(frame,index,transform,scene.Viewport,quality);
            using var deep = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan);
            Assert.Equal(ordinary, ScanIdentityVerifier.Verify(frame,index,transform,scene.Viewport,deep));
        }
    }

    [Fact]
    public void TwoClippedArmsCanUseSeparationBetweenVisibleBends()
    {
        var reference = new DeepScanStructureIndex.Corner(new(50, 50), new(50, 150), new(150, 150));
        var other = new DeepScanStructureIndex.Corner(new(150, 150), new(200, 150), new(200, 250));
        var live = new DeepScanStructureIndex.Corner(new(480, 250), new(480, 290), new(520, 290));
        var liveOther = new DeepScanStructureIndex.Corner(new(680, 290), new(720, 290), new(720, 330));
        Assert.True(DeepScanStructureIndex.TryCornerPairPose(reference, other, live, liveOther,
            ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan), out var scale, out var x, out var y));
        Assert.Equal(1.6, scale, 8);
        Assert.Equal(400, x, 8);
        Assert.Equal(50, y, 8);
        // The same length with a different direction cannot propose a rotated map.
        Assert.False(DeepScanStructureIndex.TryCornerPairPose(reference, other, live,
            liveOther with { B = new(720, 320) }, ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan), out _, out _, out _));
    }

    [Fact]
    public void CompactMarkerMaskPreservesNearbyWallAndMasksEnemyRim()
    {
        using var image = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(80, 100, 20, 20), new Scalar(20, 230, 255), -1);
        Cv2.Rectangle(image, new Rect(180, 100, 20, 20), new Scalar(50, 50, 240), -1);
        using var observation = new Vpsg3LiveObservation(new Mat(240,320,MatType.CV_8UC1,Scalar.White),
            new Mat(240,320,MatType.CV_8UC1,Scalar.White),320,240,320*240,320*240,new(0,0,320,240));
        DeepScanLiveEvidence.MaskAnnotations(image, observation);
        Assert.Equal(0, observation.ObservedEdges.At<byte>(110,90));
        Assert.Equal(255, observation.ObservedEdges.At<byte>(90,90));
        Assert.Equal(0, observation.ObservedEdges.At<byte>(94,190));
        Assert.Equal(0, observation.ValidMask.At<byte>(94,190));
    }

    [Fact]
    public void ChestExclusionDoesNotMaskThinBlueWallHighlights()
    {
        using var image = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(80, 100, 26, 16), new Scalar(195,169,172), -1);
        Cv2.Rectangle(image, new Rect(180, 100, 30, 3), new Scalar(195,169,172), -1);
        using var observation = new Vpsg3LiveObservation(new Mat(240,320,MatType.CV_8UC1,Scalar.White),
            new Mat(240,320,MatType.CV_8UC1,Scalar.White),320,240,320*240,320*240,new(0,0,320,240));
        DeepScanLiveEvidence.MaskAnnotations(image, observation);
        Assert.Equal(0, observation.ObservedEdges.At<byte>(110,90));
        Assert.Equal(255, observation.ObservedEdges.At<byte>(101,190));
    }

    [Fact]
    public void ErasedReferenceAnchorIsUnknownWithoutHidingOtherWallConflicts()
    {
        using var image = new Mat(400, 600, MatType.CV_8UC3, new Scalar(30, 25, 22));
        Cv2.Rectangle(image, new Rect(50, 70, 320, 200), new Scalar(110, 97, 88), -1);
        Cv2.Rectangle(image, new Rect(440, 140, 40, 40), new Scalar(110, 97, 88), -1);
        var viewport = new MapScreenRect(0, 0, 600, 400);
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan);
        using var frame = new ScanFrameEvidence(image, viewport, [], context.Policy);
        using var reference = frame.Observation.ObservedEdges.Clone();
        var anchor = new Rect(430, 130, 60, 60);
        Cv2.Rectangle(reference, anchor, Scalar.Black, -1);
        var ordinary = ScanStructureIndex.Get(reference);
        var deep = ordinary.WithUnknownBounds(anchor);
        var transform = new MapOverlayTransform { ScaleX = 1, ScaleY = 1 };
        Assert.Equal(ScanIdentityState.Excluded,
            ScanIdentityVerifier.Verify(frame, ordinary, transform, viewport, context).State);
        var evidence = ScanIdentityVerifier.Verify(frame, deep, transform, viewport, context);
        Assert.Equal(ScanIdentityState.Supported, evidence.State);
        Assert.True(evidence.UnknownReferencePoints > 80);
        Assert.Equal(frame.DensePoints.Length, evidence.TestedPoints + evidence.UnknownReferencePoints);
        Assert.Equal(1, evidence.SupportedFraction);
        Assert.False(ordinary.IsUnknown(450, 150));
        Assert.Same(ordinary, ScanStructureIndex.Get(reference));
        // A real missing wall outside the authored exclusion still rejects.
        using var wrong = reference.Clone();
        Cv2.Rectangle(wrong, new Rect(150, 60, 100, 30), Scalar.Black, -1);
        Assert.Equal(ScanIdentityState.Excluded, ScanIdentityVerifier.Verify(frame,
            ScanStructureIndex.Get(wrong).WithUnknownBounds(anchor), transform, viewport, context).State);
        // An oversized unknown area cannot manufacture a perfect identity.
        Assert.Equal(ScanIdentityState.Unverified, ScanIdentityVerifier.Verify(frame,
            ordinary.WithUnknownBounds(new(0, 0, 600, 400)), transform, viewport, context).State);
    }

    [Fact]
    public void UnrelatedLargeAnchorCannotEraseVisibleWallsAroundMeasuredIcon()
    {
        using var scene = new Scene(1);
        var unrelated = CreateMap();
        MapFloorRules.GetFloorProfile(unrelated, "1f")!.FindAnchor("side-entrance")!.Bounds =
            new() { X = .1, Y = .1, Width = 53d / 1200, Height = 53d / 900 };
        var gate = new GateDetection
        {
            ScreenBounds = new(scene.Viewport.X + 90, scene.Viewport.Y + 65, 20, 20)
        };
        DeepScanStructureIndex.Prewarm(scene.Line);
        Point[] originalPoints;
        using (var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan))
        {
            DeepScanPipeline.Run(scene.Frame, [(scene.Map, "1f", scene.Line)],
                scene.Viewport, context, gates: [gate]);
            originalPoints = context.Frame!.DensePoints;
            Assert.True(originalPoints.Length > 100);
            Assert.Equal(0, context.Frame.Observation.ValidMask.At<byte>(75, 100));
        }
        using (var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan))
        {
            DeepScanPipeline.Run(scene.Frame,
                [(scene.Map, "1f", scene.Line), (unrelated, "1f", scene.Line)],
                scene.Viewport, context, gates: [gate]);
            Assert.Equal(originalPoints, context.Frame!.DensePoints);
        }
    }

    [Fact]
    public void FogTruncatedArmDoesNotDetermineScaleOrRejectTheWholeCorner()
    {
        var reference = new DeepScanStructureIndex.Corner(new(80, 50), new(80, 150), new(180, 150));
        var live = new DeepScanStructureIndex.Corner(new(528, 130), new(528, 290), new(600, 290));
        var policy = ScanExecutionPolicy.For(ScanPerformanceMode.DeepScan);
        Assert.False(DeepScanStructureIndex.TryPose(reference, live, policy, out _, out _, out _));
        Assert.True(DeepScanStructureIndex.TryPartialPose(reference, live, policy, out var scale, out var x, out var y));
        Assert.Equal(1.6, scale, 8);
        Assert.Equal(400, x, 8);
        Assert.Equal(50, y, 8);
    }

    [Fact]
    public void SameCornerGeometryAtAnotherLocationRemainsAQueryLandmark()
    {
        Point[][] contours = [
            [new(20, 20), new(60, 20), new(60, 60), new(20, 60)],
            [new(120, 120), new(160, 120), new(160, 160), new(120, 160)]];
        var corners = DeepScanStructureIndex.SelectQueryCorners(contours, 200, 200);
        Assert.Contains(corners, c => c.B == new Point(20, 20));
        Assert.Contains(corners, c => c.B == new Point(120, 120));
    }

    [Theory]
    [InlineData(.65)]
    [InlineData(1)]
    [InlineData(1.6)]
    public void SmallDoorlessRegionRecoversItsOwnFloorPose(double scale)
    {
        using var scene = new Scene(scale);
        DeepScanStructureIndex.Prewarm(scene.Line);
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan);
        var candidate = Assert.Single(DeepScanPipeline.Run(scene.Frame,
            [(scene.Map, "1f", scene.Line)], scene.Viewport, context));
        Assert.True(context.RetrievalCompleted);
        Assert.Null(candidate.AssociatedGate);
        Assert.InRange(candidate.MatchScale, scale - .025, scale + .025);
        Assert.InRange(candidate.MatchLocation.X, scene.X - 4, scene.X + 4);
        Assert.InRange(candidate.MatchLocation.Y, scene.Y - 4, scene.Y + 4);
        Assert.True(SideEntranceScanPipeline.TryCreateAlignmentSeed(candidate, scene.Viewport, out var seed, out var failure), failure);
        var evidence = ScanIdentityVerifier.Verify(context.Frame!, candidate.StructureIndex!,
            seed.LockedTransform, scene.Viewport, context);
        Assert.Equal(ScanIdentityState.Supported, evidence.State);
        Assert.InRange(seed.LockedTransform.OffsetX, scene.X + scene.Viewport.X - 4, scene.X + scene.Viewport.X + 4);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([candidate], true, true)); // Formal alignment still required.
    }

    [Fact]
    public void RepeatedLocalGeometryCannotChooseBetweenMaps()
    {
        using var scene = new Scene(1);
        using var twinLine = scene.Line.Clone();
        var twin = CreateMap();
        DeepScanStructureIndex.Prewarm(scene.Line);
        DeepScanStructureIndex.Prewarm(twinLine);
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan);
        var candidates = DeepScanPipeline.Run(scene.Frame,
            [(scene.Map, "1f", scene.Line), (twin, "1f", twinLine)], scene.Viewport, context);
        Assert.Equal(2, candidates.Count);
        foreach (var candidate in candidates)
        {
            Assert.True(SideEntranceScanPipeline.TryCreateAlignmentSeed(candidate, scene.Viewport, out var seed, out _));
            candidate.IdentityEvidence = ScanIdentityVerifier.Verify(context.Frame!, candidate.StructureIndex!,
                seed.LockedTransform, scene.Viewport, context);
            Assert.Equal(ScanIdentityState.Supported, candidate.IdentityEvidence.State);
            candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
        }
        Assert.Null(ScanIdentityVerifier.SelectIdentity(candidates, context.RetrievalCompleted, true));
    }

    [Fact]
    public void ColdIndexFailsClosedWithoutBuildingOnScanThreadOrTouchingOrdinaryIndex()
    {
        using var scene = new Scene(1);
        var ordinary = ScanStructureIndex.Get(scene.Line);
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan);
        var candidate = Assert.Single(DeepScanPipeline.Run(scene.Frame,
            [(scene.Map, "1f", scene.Line)], scene.Viewport, context));
        Assert.False(context.RetrievalCompleted);
        Assert.Equal(ScanIdentityState.Unverified, candidate.IdentityEvidence.State);
        Assert.False(DeepScanStructureIndex.TryGet(scene.Line, out _));
        Assert.Same(ordinary, ScanStructureIndex.Get(scene.Line));
    }

    [Fact]
    public void CancelledAndExpiredRequestsDoNotPublishOrExtractFrames()
    {
        using var scene = new Scene(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan, cancelled.Token))
        {
            Assert.Empty(DeepScanPipeline.Run(scene.Frame, [(scene.Map, "1f", scene.Line)], scene.Viewport, context));
            Assert.False(context.RetrievalCompleted);
            Assert.Null(context.Frame);
        }
        using var expired = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan,
            startedTimestamp: Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3);
        Assert.Empty(DeepScanPipeline.Run(scene.Frame, [(scene.Map, "1f", scene.Line)], scene.Viewport, expired));
        Assert.Null(expired.Frame);
    }

    [Fact]
    public void WarmupDoesNotModifyReferenceAndCannotLeakAcrossFloorOrRevision()
    {
        using var scene = new Scene(1);
        using var otherFloor = scene.Line.Clone();
        using var before = scene.Line.Clone();
        DeepScanStructureIndex.Prewarm(scene.Line);
        Assert.Equal(0, Cv2.Norm(before, scene.Line, NormTypes.INF));
        Assert.True(DeepScanStructureIndex.TryGet(scene.Line, out var original));
        Assert.False(DeepScanStructureIndex.TryGet(otherFloor, out _));
        DeepScanStructureIndex.Prewarm(otherFloor);
        Assert.True(DeepScanStructureIndex.TryGet(otherFloor, out var other));
        Assert.NotSame(original, other);
    }

    [Theory]
    [InlineData(ScanPerformanceMode.Fast)]
    [InlineData(ScanPerformanceMode.Balanced)]
    [InlineData(ScanPerformanceMode.Quality)]
    public void ExistingModesNeverEnterDeepRetrieval(ScanPerformanceMode mode)
    {
        using var scene = new Scene(1);
        using var context = ScanExecutionContext.Enter(mode);
        Assert.Throws<InvalidOperationException>(() => DeepScanPipeline.Run(scene.Frame,
            [(scene.Map, "1f", scene.Line)], scene.Viewport, context));
        Assert.Null(context.Frame);
        Assert.Empty(new SideEntranceScanPipeline().RunScan(scene.Frame, [(scene.Map, "1f", scene.Line)]));
    }

    [Fact]
    public void EmptyOrStraightWallCannotEstablishMapIdentity()
    {
        using var scene = new Scene(1);
        scene.Frame.SetTo(new Scalar(30, 25, 22));
        DeepScanStructureIndex.Prewarm(scene.Line);
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.DeepScan);
        Assert.Empty(DeepScanPipeline.Run(scene.Frame, [(scene.Map, "1f", scene.Line)], scene.Viewport, context));
        Assert.Empty(DeepScanStructureIndex.ExtractCorners([new[] { new Point(10, 20), new Point(100, 20), new Point(200, 20) }]));
    }

    [Fact]
    public void ClippedImageBorderCannotCreateAnAuthoredCorner()
    {
        Point[][] clipped = [[new(0, 30), new(90, 30), new(90, 99), new(0, 99)]];
        Assert.Empty(DeepScanStructureIndex.SelectQueryCorners(clipped, 100, 100));
    }

    [Fact]
    public void InvalidDeepIndexDoesNotPoisonAnotherFloorOrOrdinaryDistanceIndex()
    {
        using var scene = new Scene(1);
        var ordinary = ScanStructureIndex.Get(scene.Line);
        using var invalid = new Mat(30, 30, MatType.CV_8UC3, Scalar.Black);
        Assert.Throws<OpenCVException>(() => DeepScanStructureIndex.Prewarm(invalid));
        Assert.False(DeepScanStructureIndex.TryGet(invalid, out _));
        DeepScanStructureIndex.Prewarm(scene.Line);
        Assert.True(DeepScanStructureIndex.TryGet(scene.Line, out var deep));
        Assert.Same(ordinary, deep!.Distances);
    }

    private static MapRecord CreateMap()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow,
            Floors = [new() { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
            Recognition = new() { FirstFloor = new() { FloorKey = "1f", RecognitionPixelWidth = 1200, RecognitionPixelHeight = 900 } }
        };
        map.NormalizeRecognition();
        return map;
    }

    private sealed class Scene : IDisposable
    {
        public Mat Line { get; } = new(900, 1200, MatType.CV_8UC1, Scalar.Black);
        public Mat Frame { get; }
        public MapRecord Map { get; } = CreateMap();
        public double X { get; }
        public double Y { get; }
        public MapScreenRect Viewport => new(832, 270, Frame.Width, Frame.Height);
        public Scene(double scale)
        {
            // Only 200 x 150 reference pixels are visible: under 3% of this floor.
            Point[] room = [new(510, 360), new(630, 360), new(630, 400),
                new(585, 400), new(585, 440), new(510, 440)];
            Cv2.Polylines(Line, [room], true, Scalar.White, 1);
            Cv2.Rectangle(Line, new Rect(100, 100, 230, 170), Scalar.White, 1);
            X = -480 * scale; Y = -330 * scale;
            Frame = new((int)Math.Ceiling(150 * scale), (int)Math.Ceiling(200 * scale),
                MatType.CV_8UC3, new Scalar(30, 25, 22));
            var live = room.Select(p => new Point((int)Math.Round(p.X * scale + X), (int)Math.Round(p.Y * scale + Y))).ToArray();
            Cv2.FillPoly(Frame, [live], new Scalar(110, 97, 88));
        }
        public void Dispose() { Line.Dispose(); Frame.Dispose(); }
    }
}
