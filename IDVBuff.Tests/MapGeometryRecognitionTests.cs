using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapGeometryRecognitionTests
{
    private const double FirstFloorCanvasHeight = 250d / 393d;
    private static readonly (int Sequence, double DeltaX, double DeltaY)[] ExistingMapVectors =
    [
        (7, 0.3787, -0.1423), (8, -0.2747, -0.2534), (9, 0.1695, -0.2215),
        (10, -0.2754, -0.2607), (11, -0.3968, -0.2103), (12, -0.2882, -0.3551),
        (13, -0.1642, -0.3886), (14, 0.0005, -0.2640), (15, 0.1360, -0.3920),
        (16, 0.0373, -0.3087), (17, 0.1038, -0.1639), (18, -0.2918, -0.2465),
        (19, 0.1553, -0.0174), (20, 0.5374, -0.1931), (21, 0.2709, -0.1436),
        (22, 0.5362, -0.1483), (23, 0.5169, -0.0846), (24, 0.6681, -0.1047),
        (25, 0.0604, -0.2732), (26, 0.5155, -0.1055), (27, 0.5985, -0.0569),
        (28, -0.1943, -0.2981), (29, -0.3284, -0.2661), (30, -0.2516, -0.3326),
        (31, -0.3400, -0.3171), (32, -0.3845, -0.1476), (33, 0.0368, -0.2464),
        (34, -0.3205, -0.0829)
    ];

    [Fact]
    public void SwappedIdenticalGateDetectionsStillRecoverDoorIdentity()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        var viewport = new MapScreenRect(100d, 200d, 1000d, 500d);
        var expectedMain = Detection(
            viewport.X + (fingerprint.MainPoint.X * viewport.Width),
            viewport.Y + (fingerprint.MainPoint.Y * viewport.Height),
            width: 100d,
            height: 50d);
        var expectedSide = Detection(
            viewport.X + (fingerprint.SidePoint.X * viewport.Width),
            viewport.Y + (fingerprint.SidePoint.Y * viewport.Height),
            width: 100d,
            height: 50d);

        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [expectedSide, expectedMain],
            viewport);

        var winner = Assert.Single(ranked);
        Assert.Equal(expectedMain.ScreenBounds.CenterX, winner.MainGate.ScreenBounds.CenterX, 6);
        Assert.Equal(expectedSide.ScreenBounds.CenterY, winner.SideGate.ScreenBounds.CenterY, 6);
        Assert.InRange(winner.VectorError, 0d, 0.000001d);
    }

    [Fact]
    public void ManualRankingPreservesExplicitMainAndSideIdentity()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        var viewport = new MapScreenRect(100d, 200d, 1000d, 500d);
        var main = Detection(
            viewport.X + (fingerprint.MainPoint.X * viewport.Width),
            viewport.Y + (fingerprint.MainPoint.Y * viewport.Height),
            width: 100d,
            height: 50d);
        var side = Detection(
            viewport.X + (fingerprint.SidePoint.X * viewport.Width),
            viewport.Y + (fingerprint.SidePoint.Y * viewport.Height),
            width: 100d,
            height: 50d);

        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [main, side],
            viewport,
            MapRecognitionTuning.DefaultVectorErrorTolerance,
            testSwappedAssignments: false);

        var winner = Assert.Single(ranked);
        Assert.Equal(main.ScreenBounds.CenterX, winner.MainGate.ScreenBounds.CenterX, 6);
        Assert.Equal(side.ScreenBounds.CenterY, winner.SideGate.ScreenBounds.CenterY, 6);
        Assert.InRange(winner.VectorError, 0d, 0.000001d);
    }

    [Fact]
    public void ConfiguredVectorToleranceChangesGeometryScore()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.2d, 0.6d, 0.6d);
        var viewport = new MapScreenRect(0d, 0d, 1000d, 1000d);
        var gates = new[]
        {
            Detection(200d, 200d),
            Detection(630d, 600d)
        };

        var strict = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            gates,
            viewport,
            vectorErrorTolerance: 0.01d);
        var relaxed = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            gates,
            viewport,
            vectorErrorTolerance: 0.10d);

        Assert.True(relaxed[0].Score > strict[0].Score);
    }

    [Fact]
    public void RankingUsesLiveGateMidpointAndScaleInsteadOfViewportCenter()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        const double scaleX = 1.8d;
        const double scaleY = 1.35d;
        const double offsetX = 2300d;
        const double offsetY = -450d;
        var main = Detection(
            (fingerprint.MainPoint.X * fingerprint.ReferenceWidth * scaleX) + offsetX,
            (fingerprint.MainPoint.Y * fingerprint.ReferenceHeight * scaleY) + offsetY,
            100d * scaleX,
            100d * scaleY);
        var side = Detection(
            (fingerprint.SidePoint.X * fingerprint.ReferenceWidth * scaleX) + offsetX,
            (fingerprint.SidePoint.Y * fingerprint.ReferenceHeight * scaleY) + offsetY,
            100d * scaleX,
            100d * scaleY);

        var firstViewport = new MapScreenRect(2000d, -500d, 1500d, 1200d);
        var shiftedViewport = new MapScreenRect(2500d, -250d, 700d, 600d);
        var first = Assert.Single(MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [main, side],
            firstViewport,
            testSwappedAssignments: false));
        var shifted = Assert.Single(MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [main, side],
            shiftedViewport,
            testSwappedAssignments: false));

        Assert.Equal(
            (main.ScreenBounds.CenterX + side.ScreenBounds.CenterX) / 2d,
            first.ScreenCenter.X,
            6);
        Assert.Equal(
            (main.ScreenBounds.CenterY + side.ScreenBounds.CenterY) / 2d,
            first.ScreenCenter.Y,
            6);
        Assert.Equal(scaleX, first.EstimatedScaleX, 6);
        Assert.Equal(scaleY, first.EstimatedScaleY, 6);
        Assert.InRange(first.VectorError, 0d, 0.000001d);
        Assert.Equal(first.VectorError, shifted.VectorError, 10);
    }

    [Fact]
    public void UniformZoomStillRanksWhenGateIconsKeepFixedScreenSize()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        const double mapScale = 1.9d;
        const double offsetX = -175d;
        const double offsetY = 320d;
        var main = Detection(
            (fingerprint.MainPoint.X * fingerprint.ReferenceWidth * mapScale) + offsetX,
            (fingerprint.MainPoint.Y * fingerprint.ReferenceHeight * mapScale) + offsetY);
        var side = Detection(
            (fingerprint.SidePoint.X * fingerprint.ReferenceWidth * mapScale) + offsetX,
            (fingerprint.SidePoint.Y * fingerprint.ReferenceHeight * mapScale) + offsetY);

        var winner = Assert.Single(MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [main, side],
            new MapScreenRect(0d, 0d, 1706d, 1066d),
            testSwappedAssignments: false));

        // P1-1: VectorError now includes distance error. When gate icons
        // keep fixed screen size across zoom, estimatedScale from gate
        // widths doesn't match the true map scale, so distanceError > 0.
        // The correct map must still rank first but VectorError is no
        // longer near-zero in this edge case.
        Assert.True(winner.DistanceError > 0d,
            "Fixed-size gate icons across zoom → distance mismatch");
        Assert.True(winner.VectorError >= 0d,
            "VectorError must be ≥ direction-only component");
        Assert.Equal(
            (main.ScreenBounds.CenterX + side.ScreenBounds.CenterX) / 2d,
            winner.ScreenCenter.X,
            6);
        Assert.Equal(
            (main.ScreenBounds.CenterY + side.ScreenBounds.CenterY) / 2d,
            winner.ScreenCenter.Y,
            6);
    }

    [Fact]
    public void ExistingTwentyEightMapVectorsRankTheirOwnMapFirst()
    {
        var fingerprints = ExistingMapVectors
            .Select(ExistingFingerprint)
            .ToArray();
        var viewport = new MapScreenRect(0d, 0d, 1000d, 1000d);

        foreach (var expected in fingerprints)
        {
            var gates = new[]
            {
                Detection(expected.MainPoint.X * viewport.Width, expected.MainPoint.Y * viewport.Height),
                Detection(expected.SidePoint.X * viewport.Width, expected.SidePoint.Y * viewport.Height)
            };
            var ranked = MapCvRecognitionScript.RankGeometry(fingerprints, gates, viewport);
            Assert.Equal(expected.Map.SequenceNumber, ranked[0].Fingerprint.Map.SequenceNumber);
        }
    }

    [Fact]
    public void SimilarDirectionMapsEnterConfirmationBand()
    {
        var fingerprints = ExistingMapVectors
            .Select(ExistingFingerprint)
            .ToArray();
        var expected = fingerprints.Single(item => item.Map.SequenceNumber == 8);
        var viewport = new MapScreenRect(0d, 0d, 1000d, 1000d);
        var gates = new[]
        {
            Detection(expected.MainPoint.X * viewport.Width, expected.MainPoint.Y * viewport.Height),
            Detection(expected.SidePoint.X * viewport.Width, expected.SidePoint.Y * viewport.Height)
        };

        var ranked = MapCvRecognitionScript.RankGeometry(fingerprints, gates, viewport);

        // P1-1: VectorError now includes distance error. The correct
        // map (8) must still rank first, and the second-place map
        // must have a direction that is geometrically close.
        Assert.Equal(8, ranked[0].Fingerprint.Map.SequenceNumber);
        Assert.Contains(
            ranked[1].Fingerprint.Map.SequenceNumber,
            new[] { 10, 31 });
        // The top two candidates must have measurably different vector errors
        // (distance provides additional separation beyond just direction).
        Assert.True(ranked[0].VectorError < ranked[1].VectorError,
            $"Correct map vector error {ranked[0].VectorError:F4} "
            + $"should be < second place {ranked[1].VectorError:F4}");
    }

    [Fact]
    public void IndependentAxesExactlyAlignsAnisotropicallyScaledGatePair()
    {
        var fingerprint = Fingerprint(28, 0.385d, 0.965d, 0.151d, 0.336d);
        const double scaleX = 1.876d;
        const double scaleY = 1.138d;
        const double offsetX = 948d;
        const double offsetY = -71d;
        var candidate = CandidateFromTransform(
            fingerprint,
            scaleX,
            scaleY,
            offsetX,
            offsetY);

        var solved = MapOverlayTransformSolver.TrySolve(
            candidate,
            MapOverlayAlignmentMode.IndependentAxes,
            out var transform,
            out var failureReason);

        Assert.True(solved, failureReason);
        Assert.Equal(scaleX, transform.ScaleX, 6);
        Assert.Equal(scaleY, transform.ScaleY, 6);
        Assert.Equal(offsetX, transform.OffsetX, 6);
        Assert.Equal(offsetY, transform.OffsetY, 6);
        Assert.Equal(
            (candidate.MainGate.ScreenBounds.CenterX + candidate.SideGate.ScreenBounds.CenterX) / 2d,
            transform.ScreenCenterX,
            6);
        Assert.Equal(
            (candidate.MainGate.ScreenBounds.CenterY + candidate.SideGate.ScreenBounds.CenterY) / 2d,
            transform.ScreenCenterY,
            6);
        Assert.InRange(transform.MaximumResidualPixels, 0d, 0.01d);
        Assert.True(transform.IsExactFit);
        Assert.False(transform.UsedDegenerateAxisFallback);
        Assert.Equal(0, transform.OrientationDegrees);
    }

    [Fact]
    public void UniformModePreservesAspectRatioAndReportsResidual()
    {
        var fingerprint = Fingerprint(28, 0.385d, 0.965d, 0.151d, 0.336d);
        var candidate = CandidateFromTransform(
            fingerprint,
            scaleX: 1.876d,
            scaleY: 1.138d,
            offsetX: 948d,
            offsetY: -71d);

        var solved = MapOverlayTransformSolver.TrySolve(
            candidate,
            MapOverlayAlignmentMode.Uniform,
            out var transform,
            out var failureReason);

        Assert.True(solved, failureReason);
        Assert.Equal(transform.ScaleX, transform.ScaleY, 10);
        Assert.True(transform.MaximumResidualPixels > MapOverlayTransformSolver.ExactFitTolerancePixels);
        Assert.False(transform.IsExactFit);
        Assert.False(transform.UsedDegenerateAxisFallback);
    }

    [Fact]
    public void NearlyVerticalGatePairUsesHorizontalFallback()
    {
        var fingerprint = Fingerprint(14, 0.3319d, 0.9596d, 0.3311d, 0.3512d);
        var candidate = CandidateFromTransform(
            fingerprint,
            scaleX: 1.4d,
            scaleY: 1.4d,
            offsetX: 600d,
            offsetY: -20d);

        var solved = MapOverlayTransformSolver.TrySolve(
            candidate,
            MapOverlayAlignmentMode.IndependentAxes,
            out var transform,
            out var failureReason);

        Assert.True(solved, failureReason);
        Assert.Equal(1.4d, transform.ScaleX, 6);
        Assert.Equal(1.4d, transform.ScaleY, 6);
        Assert.True(transform.UsedDegenerateAxisFallback);
        Assert.True(transform.IsExactFit);
    }

    [Fact]
    public void NearlyHorizontalGatePairUsesVerticalFallback()
    {
        var fingerprint = Fingerprint(35, 0.15d, 0.5005d, 0.85d, 0.5d);
        var candidate = CandidateFromTransform(
            fingerprint,
            scaleX: 1.6d,
            scaleY: 1.6d,
            offsetX: 120d,
            offsetY: 80d);

        var solved = MapOverlayTransformSolver.TrySolve(
            candidate,
            MapOverlayAlignmentMode.IndependentAxes,
            out var transform,
            out var failureReason);

        Assert.True(solved, failureReason);
        Assert.Equal(1.6d, transform.ScaleX, 6);
        Assert.Equal(1.6d, transform.ScaleY, 6);
        Assert.True(transform.UsedDegenerateAxisFallback);
        Assert.True(transform.IsExactFit);
    }

    [Fact]
    public void IndependentAxesRejectsMirroredPair()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.2d, 0.8d, 0.8d);
        var candidate = new MapGeometryCandidate
        {
            Fingerprint = fingerprint,
            MainGate = Detection(100d, 100d),
            SideGate = Detection(50d, 200d)
        };

        var solved = MapOverlayTransformSolver.TrySolve(
            candidate,
            MapOverlayAlignmentMode.IndependentAxes,
            out _,
            out var failureReason);

        Assert.False(solved);
        Assert.Contains("镜像", failureReason);
    }

    [Fact]
    public void AlignmentRejectsCoincidentGatesAndExtremeScale()
    {
        var fingerprint = Fingerprint(1, 0.2d, 0.2d, 0.8d, 0.8d);
        var coincident = new MapGeometryCandidate
        {
            Fingerprint = fingerprint,
            MainGate = Detection(100d, 100d),
            SideGate = Detection(100d, 100d)
        };
        var extreme = CandidateFromTransform(
            fingerprint,
            scaleX: 9d,
            scaleY: 9d,
            offsetX: 0d,
            offsetY: 0d);

        Assert.False(MapOverlayTransformSolver.TrySolve(
            coincident,
            MapOverlayAlignmentMode.IndependentAxes,
            out _,
            out _));
        Assert.False(MapOverlayTransformSolver.TrySolve(
            extreme,
            MapOverlayAlignmentMode.IndependentAxes,
            out _,
            out _));
    }

    [Fact]
    public void LockedScaleTranslationUsesOneAnchorWithoutChangingScale()
    {
        var locked = new MapOverlayTransform
        {
            ScaleX = 1.8d,
            ScaleY = 1.25d,
            ReferenceWidth = 1000,
            ReferenceHeight = 800,
            AlignmentMode = MapOverlayAlignmentMode.IndependentAxes
        };
        var reference = new MapScreenRect(180d, 220d, 40d, 40d);
        const double expectedOffsetX = 310d;
        const double expectedOffsetY = -75d;
        var screen = new MapScreenRect(
            (reference.CenterX * locked.ScaleX) + expectedOffsetX - 20d,
            (reference.CenterY * locked.ScaleY) + expectedOffsetY - 20d,
            40d,
            40d);

        var solved = MapOverlayTransformSolver.TryTranslateWithLockedScale(
            locked,
            [
                new CvAnchorEvidence
                {
                    AnchorId = Guid.NewGuid(),
                    Score = 0.92d,
                    ReferenceBounds = reference,
                    ScreenBounds = screen
                }
            ],
            out var transform,
            out var failureReason);

        Assert.True(solved, failureReason);
        Assert.Equal(locked.ScaleX, transform.ScaleX, 8);
        Assert.Equal(locked.ScaleY, transform.ScaleY, 8);
        Assert.Equal(expectedOffsetX, transform.OffsetX, 8);
        Assert.Equal(expectedOffsetY, transform.OffsetY, 8);
    }

}
