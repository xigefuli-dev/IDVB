using System.Drawing;
using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3_5;

public sealed class Vpsg3_5TrackingPhase2Tests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3_5TrackingPhase2Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static MapRecord CreateTestMap(Guid id) =>
        new()
        {
            Id = id,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static Vpsg3IndexCacheKey MakeKey(string refName, string floor = "1f") =>
        new(Guid.NewGuid(), floor, "hash_" + refName, DateTimeOffset.UtcNow, "gen_" + refName);

    [Fact]
    public void Test1_MapAlignmentSession_AcceptsVpsgTracking_AndMaintainsScaleLock()
    {
        var mapId = Guid.NewGuid();
        var map = CreateTestMap(mapId);
        const double lockedScale = 0.8542d;
        const double initialTx = 320.0d;
        const double initialTy = 240.0d;

        var initialTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale,
            lockedScale,
            initialTx,
            initialTy,
            1000,
            1000,
            residualPixels: 0d,
            orientationDegrees: 0,
            alignmentMode: MapOverlayAlignmentMode.Uniform);

        var initialResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.95d,
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = initialTransform
        };

        var session = MapAlignmentSession.FromRecognition(map, initialResult);
        Assert.Equal(MapAlignmentTrackingMode.StructureMatched, session.Mode);
        Assert.Equal(lockedScale, session.BaselineGateScale, 4);

        // Advance with VpsgTracking
        const double newTx = 345.0d;
        const double newTy = 230.0d;
        var advancedTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale,
            lockedScale,
            newTx,
            newTy,
            1000,
            1000,
            residualPixels: 0d,
            orientationDegrees: 0,
            alignmentMode: MapOverlayAlignmentMode.Uniform);

        var trackingResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.98d,
            Source = MapRecognitionSource.VpsgTracking,
            OverlayTransform = advancedTransform
        };

        var advancedSession = session.Advance(map, trackingResult, maximumScaleChangeRatio: 0.05d);

        Assert.NotNull(advancedSession);
        Assert.Equal(MapAlignmentTrackingMode.VpsgTracking, advancedSession.Mode);
        Assert.Equal(lockedScale, advancedSession.BaselineGateScale, 4);
        Assert.Equal(newTx, advancedSession.LockedTransform.OffsetX, 1);
        Assert.Equal(newTy, advancedSession.LockedTransform.OffsetY, 1);
        Assert.Equal(0, advancedSession.ConsecutiveRejections);
        _output.WriteLine($"Advance success: Mode={advancedSession.Mode}, Offset=({advancedSession.LockedTransform.OffsetX}, {advancedSession.LockedTransform.OffsetY}), Scale={advancedSession.LockedTransform.ScaleX}");
    }

    [Fact]
    public void Test2_MapAlignmentSession_FloorChangeGuard_RejectsCrossFloorTracking()
    {
        var mapId = Guid.NewGuid();
        var map = CreateTestMap(mapId);
        const double lockedScale = 0.8542d;

        var initialTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale, lockedScale, 300, 200, 1000, 1000, 0d, 0, MapOverlayAlignmentMode.Uniform);

        var initialResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.95d,
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = initialTransform
        };

        var session = MapAlignmentSession.FromRecognition(map, initialResult);

        // Attempting to advance 1F session with a 2F tracking observation must fail
        var crossFloorResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "2f", // Different floor!
            Confidence = 0.98d,
            Source = MapRecognitionSource.VpsgTracking,
            OverlayTransform = initialTransform
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.Advance(map, crossFloorResult, maximumScaleChangeRatio: 0.05d));

        Assert.Contains("floor change", ex.Message, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Cross-floor guard verified: {ex.Message}");
    }

    [Fact]
    public void Test3_MapAlignmentSession_ScaleDriftGuard_RejectsScaleTampering()
    {
        var mapId = Guid.NewGuid();
        var map = CreateTestMap(mapId);
        const double lockedScale = 0.8000d;

        var initialTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale, lockedScale, 300, 200, 1000, 1000, 0d, 0, MapOverlayAlignmentMode.Uniform);

        var initialResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.95d,
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = initialTransform
        };

        var session = MapAlignmentSession.FromRecognition(map, initialResult);

        // Tampered scale: 0.92 (>15% change, exceeding 5% threshold)
        var tamperedTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            0.9200d, 0.9200d, 310, 205, 1000, 1000, 0d, 0, MapOverlayAlignmentMode.Uniform);

        var tamperedResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.98d,
            Source = MapRecognitionSource.VpsgTracking,
            OverlayTransform = tamperedTransform
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            session.Advance(map, tamperedResult, maximumScaleChangeRatio: 0.05d));

        Assert.Contains("tracking scale changed", ex.Message, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Scale tampering guard verified: {ex.Message}");
    }

    [Fact]
    public void Test4_StationaryMotionGate_BehavioralVerification()
    {
        using var frame1 = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(40));
        // Draw filled structures simulating map rooms and obstacles
        Cv2.Rectangle(frame1, new Rect(100, 100, 150, 120), Scalar.All(220), -1);
        Cv2.Rectangle(frame1, new Rect(300, 200, 200, 100), Scalar.All(180), -1);

        using var frame2Identical = frame1.Clone();

        using var frame3Moved = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(40));
        // Shifted structures by 30px simulating viewport movement
        Cv2.Rectangle(frame3Moved, new Rect(130, 100, 150, 120), Scalar.All(220), -1);
        Cv2.Rectangle(frame3Moved, new Rect(330, 200, 200, 100), Scalar.All(180), -1);

        Mat? previousGray = null;
        try
        {
            // First frame: initial priming, returns false
            var stationary0 = TestStationaryViewport(frame1, ref previousGray);
            Assert.False(stationary0);
            Assert.NotNull(previousGray);

            // Second identical frame: should detect stationary = true
            var stationary1 = TestStationaryViewport(frame2Identical, ref previousGray);
            Assert.True(stationary1);

            // Third moved frame: should detect motion = false (not stationary)
            var stationary2 = TestStationaryViewport(frame3Moved, ref previousGray);
            Assert.False(stationary2);

            _output.WriteLine("Stationary motion gate passed: priming=false, identical=true, moved=false");
        }
        finally
        {
            previousGray?.Dispose();
        }
    }

    [Fact]
    public void Test5_EndToEnd_TrackingPipeline_ProducesAcceptedVpsgTrackingResult()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var sample = dataset.First();

        var mapId = Guid.NewGuid();
        var map = CreateTestMap(mapId);
        var key = MakeKey(sample.ReferenceName);
        using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

        using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);

        // Prior position slightly offset (+5px, -4px)
        var priorTx = sample.TrueOffsetX + 5.0d;
        var priorTy = sample.TrueOffsetY - 4.0d;
        var lockedScale = sample.TrueScale;

        var trackResult = Vpsg3_5TrackingSolver.TryTrack(
            obs,
            preparedFloor,
            lockedScale,
            priorTx,
            priorTy);

        Assert.True(trackResult.IsAccepted);

        // Build overlay transform as done in SessionOrchestrator.Vpsg3_5.Tracking.cs
        var updatedTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale,
            lockedScale,
            trackResult.OffsetX,
            trackResult.OffsetY,
            referenceWidth: 1000,
            referenceHeight: 1000,
            residualPixels: 0d,
            orientationDegrees: 0,
            alignmentMode: MapOverlayAlignmentMode.Uniform);

        var initialResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.90d,
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = MapCanonicalTransformMath.BuildOverlayTransform(
                lockedScale, lockedScale, priorTx, priorTy, 1000, 1000, 0d, 0, MapOverlayAlignmentMode.Uniform)
        };

        var session = MapAlignmentSession.FromRecognition(map, initialResult);

        var updatedRecognitionResult = new MapRecognitionResult
        {
            MapId = mapId,
            Floor = "1f",
            Confidence = 0.99d,
            Source = MapRecognitionSource.VpsgTracking,
            OverlayTransform = updatedTransform
        };

        var advancedSession = session.Advance(map, updatedRecognitionResult, maximumScaleChangeRatio: 0.05d);

        Assert.Equal(MapAlignmentTrackingMode.VpsgTracking, advancedSession.Mode);
        Assert.Equal(lockedScale, advancedSession.BaselineGateScale, 4);

        var error = Math.Sqrt(Math.Pow(trackResult.OffsetX - sample.TrueOffsetX, 2) + Math.Pow(trackResult.OffsetY - sample.TrueOffsetY, 2));
        Assert.True(error <= 1.5d, $"End-to-end tracking error must be <= 1.5px, actual: {error:F2}px");

        _output.WriteLine($"End-to-end pipeline passed: error={error:F2}px, mode={advancedSession.Mode}, solverMs={trackResult.Timing.TotalMs:F2}ms");
    }

    [Fact]
    public void Test6_FastContinuousDragWithVelocityInertia_MaintainsLockAndAccuracy()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var sample = dataset.First();

        var key = MakeKey(sample.ReferenceName);
        using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);
        using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);

        var lockedScale = sample.TrueScale;
        var initialTx = sample.TrueOffsetX;
        var initialTy = sample.TrueOffsetY;
        var currentTx = initialTx;
        var currentTy = initialTy;
        var velTx = 0.0d;
        var velTy = 0.0d;
        const double stepDeltaX = 8.0d; // Simulating 8px/frame continuous drag (~400 px/s at 50fps)
        const double stepDeltaY = -6.0d;

        var localHits = 0;
        var totalFrames = 10;
        var cumDx = 0d;
        var cumDy = 0d;

        for (var frame = 0; frame < totalFrames; frame++)
        {
            cumDx += stepDeltaX;
            cumDy += stepDeltaY;

            var trueStepTx = initialTx + cumDx;
            var trueStepTy = initialTy + cumDy;

            // Prediction uses velocity inertia
            var predTx = currentTx + velTx;
            var predTy = currentTy + velTy;

            // Generate shifted sparse points simulating moving viewport
            var shiftedPoints = new List<OpenCvSharp.Point>(obs.SparseEdgePoints.Count);
            var shiftX = (int)Math.Round(cumDx);
            var shiftY = (int)Math.Round(cumDy);
            for (var i = 0; i < obs.SparseEdgePoints.Count; i++)
            {
                var p = obs.SparseEdgePoints[i];
                var sx = p.X + shiftX;
                var sy = p.Y + shiftY;
                if (sx >= 0 && sx < obs.Width && sy >= 0 && sy < obs.Height)
                {
                    shiftedPoints.Add(new OpenCvSharp.Point(sx, sy));
                }
            }

            if (shiftedPoints.Count < 20)
                shiftedPoints = [.. obs.SparseEdgePoints];

            using var edgesClone = obs.ObservedEdges.Clone();
            using var maskClone = obs.ValidMask.Clone();
            using var shiftedObs = new Vpsg3LiveObservation(
                edgesClone,
                maskClone,
                obs.Width,
                obs.Height,
                obs.EdgePixelCount,
                obs.ValidStructurePixelCount,
                obs.ViewportBounds,
                maxSparsePoints: 150,
                sparseEdgePoints: shiftedPoints.ToArray());

            var trackResult = Vpsg3_5TrackingSolver.TryTrack(
                shiftedObs,
                preparedFloor,
                lockedScale,
                priorTx: predTx,
                priorTy: predTy);

            Assert.True(trackResult.IsAccepted, $"Frame {frame} tracking must be accepted");
            if (trackResult.IsLocalFastPath) localHits++;

            // Update velocity with EMA
            var stepVx = trackResult.OffsetX - currentTx;
            var stepVy = trackResult.OffsetY - currentTy;
            velTx = 0.7d * stepVx + 0.3d * velTx;
            velTy = 0.7d * stepVy + 0.3d * velTy;
            currentTx = trackResult.OffsetX;
            currentTy = trackResult.OffsetY;

            var err = Math.Sqrt(Math.Pow(trackResult.OffsetX - trueStepTx, 2) + Math.Pow(trackResult.OffsetY - trueStepTy, 2));
            Assert.True(err <= 3.5d, $"Frame {frame} error {err:F2}px must be <= 3.5px");
        }

        Assert.True(localHits >= totalFrames - 2, $"At least {totalFrames - 2} frames must take local fast path, actual: {localHits}");
        _output.WriteLine($"Fast continuous drag test passed: {localHits}/{totalFrames} local fast path hits.");
    }

    [Fact]
    public void Test7_SpatiallyInconsistentClutter_CannotHijackTrackerOrCauseRunawayDrift()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            var key = MakeKey(sample.ReferenceName);
            using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            // Create artificial clutter: 30 points clustered strictly along a single horizontal row
            // This can achieve a high local bitset hit score on a wall, but has 0 spatial quadrant coverage
            var fakePoints = new List<OpenCvSharp.Point>(30);
            for (var i = 0; i < 30; i++)
            {
                fakePoints.Add(new OpenCvSharp.Point(50 + i * 2, 50));
            }

            using var fakeEdges = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
            using var fakeMask = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(255));
            using var clutterObs = new Vpsg3LiveObservation(
                fakeEdges.Clone(),
                fakeMask.Clone(),
                240, 240, 30, 30,
                new MapScreenRect(0, 0, 240, 240),
                maxSparsePoints: 100,
                sparseEdgePoints: fakePoints.ToArray());

            var result = Vpsg3_5TrackingSolver.TryTrack(
                clutterObs,
                preparedFloor,
                sample.TrueScale,
                priorTx: sample.TrueOffsetX,
                priorTy: sample.TrueOffsetY);

            Assert.False(result.IsAccepted, "Single-quadrant or inconsistent clutter must never be accepted by tracking gate.");
            Assert.Contains("SpatialVerificationFailed", result.FallbackReason);
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    private static bool TestStationaryViewport(Mat currentBgr, ref Mat? previousGraySmall)
    {
        if (currentBgr.Empty()) return false;

        using var smallBgr = new Mat();
        Cv2.Resize(currentBgr, smallBgr, new OpenCvSharp.Size(64, 48), interpolation: InterpolationFlags.Nearest);
        var currentGraySmall = new Mat();
        Cv2.CvtColor(smallBgr, currentGraySmall, ColorConversionCodes.BGR2GRAY);

        if (previousGraySmall is null || previousGraySmall.Size() != currentGraySmall.Size())
        {
            previousGraySmall?.Dispose();
            previousGraySmall = currentGraySmall;
            return false;
        }

        using var diff = new Mat();
        Cv2.Absdiff(currentGraySmall, previousGraySmall, diff);
        Cv2.Threshold(diff, diff, 10, 255, ThresholdTypes.Binary);
        var nonZero = Cv2.CountNonZero(diff);
        previousGraySmall.Dispose();
        previousGraySmall = currentGraySmall;

        return nonZero <= (64 * 48 * 0.02);
    }
}
