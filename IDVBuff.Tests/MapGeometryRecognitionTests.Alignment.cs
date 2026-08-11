using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapGeometryRecognitionTests
{
    [Fact]
    public void DirectDualGateLockRequiresCompleteHighConfidenceEvidence()
    {
        var tuning = new MapRecognitionTuning
        {
            GateTemplateThreshold = 0.72d
        };
        var valid = new MapRecognitionResult
        {
            Confidence = 0.82d,
            HasAllRequiredAnchorEvidence = true,
            AnchorMatches =
            [
                new CvAnchorEvidence { Score = 0.91d },
                new CvAnchorEvidence { Score = 0.89d }
            ]
        };

        Assert.True(MapFastAlignmentRules.CanDirectLockDualGate(
            valid,
            tuning));
        Assert.False(MapFastAlignmentRules.CanDirectLockDualGate(
            new MapRecognitionResult
            {
                Confidence = 0.70d,
                HasAllRequiredAnchorEvidence = true,
                AnchorMatches = valid.AnchorMatches
            },
            tuning));
        Assert.False(MapFastAlignmentRules.CanDirectLockDualGate(
            new MapRecognitionResult
            {
                Confidence = 0.95d,
                HasAllRequiredAnchorEvidence = true,
                WasForcedBestResult = true,
                AnchorMatches = valid.AnchorMatches
            },
            tuning));
        Assert.False(MapFastAlignmentRules.CanDirectLockDualGate(
            new MapRecognitionResult
            {
                Confidence = 0.95d,
                HasAllRequiredAnchorEvidence = true,
                AnchorMatches =
                [
                    new CvAnchorEvidence { Score = 0.90d },
                    new CvAnchorEvidence { Score = 0.60d }
                ]
            },
            tuning));
    }

    // ═══════════════════════════════════════════════════════════════
    // P1-1: Distance error in vector error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CorrectDirectionAndDistancePasses()
    {
        // P1-1: When gate positions exactly match the expected location
        // (correct direction AND distance), VectorError must be near zero.
        // Use a square viewport and gates whose sizes track the scale so
        // that estimatedScale matches the true geometric scale.
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        const double scale = 1.5d;
        const double viewportW = 1000d;
        const double viewportH = 1000d;
        var viewport = new MapScreenRect(100d, 200d, viewportW, viewportH);
        var main = Detection(
            viewport.X + (fingerprint.MainPoint.X * viewportW * scale),
            viewport.Y + (fingerprint.MainPoint.Y * viewportH * scale),
            width: 100d * scale,
            height: 100d * scale);
        var side = Detection(
            viewport.X + (fingerprint.SidePoint.X * viewportW * scale),
            viewport.Y + (fingerprint.SidePoint.Y * viewportH * scale),
            width: 100d * scale,
            height: 100d * scale);

        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [main, side],
            viewport);

        var winner = Assert.Single(ranked);
        Assert.InRange(winner.VectorError, 0d, 0.001d);
    }

    [Fact]
    public void CorrectDirectionButWrongDistanceIsRejected()
    {
        // P1-1: Gates placed at the correct angle relative to each other
        // but at half the expected distance must produce a measurably
        // higher VectorError (previously distanceError was ignored).
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        var viewport = new MapScreenRect(100d, 200d, 1000d, 500d);
        var referenceMainX = fingerprint.MainPoint.X * viewport.Width + viewport.X;
        var referenceMainY = fingerprint.MainPoint.Y * viewport.Height + viewport.Y;
        var referenceDeltaX = (fingerprint.SidePoint.X - fingerprint.MainPoint.X) * viewport.Width;
        var referenceDeltaY = (fingerprint.SidePoint.Y - fingerprint.MainPoint.Y) * viewport.Height;

        // Place side gate at half distance but same direction.
        var main = Detection(referenceMainX, referenceMainY);
        var side = Detection(
            referenceMainX + referenceDeltaX * 0.5d,
            referenceMainY + referenceDeltaY * 0.5d);

        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [main, side],
            viewport);

        var winner = Assert.Single(ranked);
        // VectorError must be non-trivial because distance is half of expected.
        Assert.True(winner.VectorError > 0.001d,
            $"VectorError={winner.VectorError:F6} should be >0.001 because distance is halved");
        Assert.True(winner.DistanceError > 0.001d,
            $"DistanceError={winner.DistanceError:F6} should be >0.001");
    }

    [Fact]
    public void CloserDistanceCandidateRanksFirstWhenDirectionsMatch()
    {
        // P1-1: Two fingerprints with nearly identical directions but
        // different distances — the one with correct distance must rank
        // higher (lower VectorError).
        var correct = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        var distant = Fingerprint(2, 0.2d, 0.25d, 0.64d, 0.77d); // same direction, longer
        var viewport = new MapScreenRect(100d, 200d, 1000d, 500d);

        // Place gates at the correct fingerprint's position.
        var main = Detection(
            viewport.X + (correct.MainPoint.X * viewport.Width),
            viewport.Y + (correct.MainPoint.Y * viewport.Height));
        var side = Detection(
            viewport.X + (correct.SidePoint.X * viewport.Width),
            viewport.Y + (correct.SidePoint.Y * viewport.Height));

        var ranked = MapCvRecognitionScript.RankGeometry(
            [correct, distant],
            [main, side],
            viewport);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(1, ranked[0].Fingerprint.Map.SequenceNumber);
        Assert.True(ranked[0].VectorError < ranked[1].VectorError,
            $"Correct map VectorError {ranked[0].VectorError:F6} " +
            $"should be < distant map {ranked[1].VectorError:F6}");
    }

    [Fact]
    public void TranslationAndUniformScaleDoNotAffectCorrectCandidate()
    {
        // P1-1: Panning (translation) and zooming (uniform scale) must
        // not change VectorError for a correct match. Gates must have
        // sizes that track the zoom so distance error stays near zero.
        var fingerprint = Fingerprint(1, 0.2d, 0.25d, 0.42d, 0.51d);
        const double scale = 1.8d;
        const double offsetX = 500d;
        const double offsetY = -200d;
        const double viewportW = 1000d;
        const double viewportH = 1000d;
        var viewport1 = new MapScreenRect(0d, 0d, viewportW, viewportH);
        var viewport2 = new MapScreenRect(300d, 100d, 800d, 800d);

        var main1 = Detection(
            viewport1.X + (fingerprint.MainPoint.X * viewportW * scale) + offsetX,
            viewport1.Y + (fingerprint.MainPoint.Y * viewportH * scale) + offsetY,
            width: 100d * scale,
            height: 100d * scale);
        var side1 = Detection(
            viewport1.X + (fingerprint.SidePoint.X * viewportW * scale) + offsetX,
            viewport1.Y + (fingerprint.SidePoint.Y * viewportH * scale) + offsetY,
            width: 100d * scale,
            height: 100d * scale);

        var ranked1 = MapCvRecognitionScript.RankGeometry(
            [fingerprint], [main1, side1], viewport1);
        var ranked2 = MapCvRecognitionScript.RankGeometry(
            [fingerprint], [main1, side1], viewport2);

        var winner1 = Assert.Single(ranked1);
        var winner2 = Assert.Single(ranked2);
        Assert.InRange(winner1.VectorError, 0d, 0.0001d);
        Assert.InRange(winner2.VectorError, 0d, 0.0001d);
    }

    [Fact]
    public void SwappedGatesDirectionHandlingIsPreserved()
    {
        // P1-1 regression: Swapping main/side must still handle direction
        // correctly. The direction vector from main→side and from side→main
        // point opposite ways; the swapped assignment should recognise this.
        var fingerprint = Fingerprint(12, 0.15d, 0.3d, 0.55d, 0.4d);
        var viewport = new MapScreenRect(0d, 0d, 1000d, 1000d);

        var expectedMain = Detection(
            fingerprint.MainPoint.X * viewport.Width,
            fingerprint.MainPoint.Y * viewport.Height);
        var expectedSide = Detection(
            fingerprint.SidePoint.X * viewport.Width,
            fingerprint.SidePoint.Y * viewport.Height);

        // Feed gates in reversed order — swapping should recover identity.
        var ranked = MapCvRecognitionScript.RankGeometry(
            [fingerprint],
            [expectedSide, expectedMain],
            viewport);

        var winner = Assert.Single(ranked);
        Assert.InRange(winner.VectorError, 0d, 0.0001d);
        // Main gate must be the correct one, not the side gate.
        Assert.Equal(expectedMain.ScreenBounds.CenterX, winner.MainGate.ScreenBounds.CenterX, 6);
        Assert.Equal(expectedSide.ScreenBounds.CenterY, winner.SideGate.ScreenBounds.CenterY, 6);
    }

    [Fact]
    public void ClearGatesWithInsideToleranceGeometryClearLockThreshold()
    {
        // Regression for the QuickScan failure in scan-log-20260731-133605:
        // gates clearly visible (~0.887 each, DualGateEarlyExit) with geometry
        // error comfortably inside the configured tolerance (0.1043 / 0.15)
        // used to produce 45% confidence. Gates are the primary evidence, so
        // this must now clear the session lock floor.
        var confidence = MapAlignmentConfidence.ComputeDualGateConfidence(
            mainGateScore: 0.8868d,
            sideGateScore: 0.8868d,
            vectorError: 0.1043d,
            vectorErrorTolerance: 0.15d);

        Assert.True(
            confidence >= MapSessionRules.MediumConfidence,
            $"Expected confidence {confidence:P0} to clear the lock floor "
            + $"{MapSessionRules.MediumConfidence:P0}.");
    }

    [Fact]
    public void DualGateConfidenceIncreasesWithGateScore()
    {
        var withWeakGates = MapAlignmentConfidence.ComputeDualGateConfidence(
            0.72d, 0.72d, 0.1d, 0.15d);
        var withClearGates = MapAlignmentConfidence.ComputeDualGateConfidence(
            0.95d, 0.95d, 0.1d, 0.15d);

        Assert.True(
            withClearGates > withWeakGates,
            $"Clear gates {withClearGates:F3} should score higher than weak "
            + $"gates {withWeakGates:F3}.");
    }

    [Fact]
    public void DualGateConfidenceDecreasesWithVectorError()
    {
        var goodGeometry = MapAlignmentConfidence.ComputeDualGateConfidence(
            0.8868d, 0.8868d, 0.03d, 0.15d);
        var edgeGeometry = MapAlignmentConfidence.ComputeDualGateConfidence(
            0.8868d, 0.8868d, 0.149d, 0.15d);

        Assert.True(
            goodGeometry > edgeGeometry,
            $"Inside-tolerance geometry {goodGeometry:F3} should score higher "
            + $"than near-tolerance geometry {edgeGeometry:F3}.");
    }

    [Fact]
    public void GeometryGoodnessRewardsInsideToleranceMatches()
    {
        // A match at 70% of tolerance keeps roughly half its geometry credit —
        // far above the 30% the old linear (1 − v/t) penalty awarded.
        var goodness = MapCvRecognitionScript.GeometryGoodness(0.7d, 1d);
        Assert.InRange(goodness, 0.40d, 0.60d);
    }

    [Fact]
    public void DualGateConfidenceIsBoundedUnitInterval()
    {
        var perfect = MapAlignmentConfidence.ComputeDualGateConfidence(
            1d, 1d, 0d, 0.15d);
        var impossible = MapAlignmentConfidence.ComputeDualGateConfidence(
            0d, 0d, 100d, 0.15d);

        Assert.Equal(1d, perfect, 9);
        Assert.InRange(impossible, 0d, 0.01d);
    }

    private static MapGeometryFingerprint Fingerprint(
        int sequence,
        double mainX,
        double mainY,
        double sideX,
        double sideY)
    {
        const int referenceWidth = 1000;
        const int referenceHeight = 1000;
        return new()
        {
            Map = new MapRecord { Id = Guid.NewGuid(), SequenceNumber = sequence },
            MainPoint = new MapNormalizedPoint(mainX, mainY),
            SidePoint = new MapNormalizedPoint(sideX, sideY),
            MainReferenceBounds = ReferenceBounds(
                mainX,
                mainY,
                referenceWidth,
                referenceHeight),
            SideReferenceBounds = ReferenceBounds(
                sideX,
                sideY,
                referenceWidth,
                referenceHeight),
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight
        };
    }

    private static MapGeometryFingerprint ExistingFingerprint(
        (int Sequence, double DeltaX, double DeltaY) vector)
    {
        var mainX = vector.DeltaX >= 0d ? 0.1d : 0.8d;
        const double mainY = 0.8d;
        return Fingerprint(
            vector.Sequence,
            mainX,
            mainY,
            mainX + vector.DeltaX,
            mainY + (vector.DeltaY / FirstFloorCanvasHeight));
    }

    private static MapScreenRect ReferenceBounds(
        double centerX,
        double centerY,
        int referenceWidth,
        int referenceHeight) =>
        new(
            (centerX * referenceWidth) - 50d,
            (centerY * referenceHeight) - 50d,
            100d,
            100d);

    private static GateDetection Detection(
        double centerX,
        double centerY,
        double width = 100d,
        double height = 100d) =>
        new()
        {
            Score = 0.95d,
            Scale = 1d,
            ScreenBounds = new MapScreenRect(
                centerX - (width / 2d),
                centerY - (height / 2d),
                width,
                height)
        };

    private static MapGeometryCandidate CandidateFromTransform(
        MapGeometryFingerprint fingerprint,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY)
    {
        var mainX = (fingerprint.MainPoint.X * fingerprint.ReferenceWidth * scaleX) + offsetX;
        var mainY = (fingerprint.MainPoint.Y * fingerprint.ReferenceHeight * scaleY) + offsetY;
        var sideX = (fingerprint.SidePoint.X * fingerprint.ReferenceWidth * scaleX) + offsetX;
        var sideY = (fingerprint.SidePoint.Y * fingerprint.ReferenceHeight * scaleY) + offsetY;
        return new MapGeometryCandidate
        {
            Fingerprint = fingerprint,
            MainGate = Detection(mainX, mainY),
            SideGate = Detection(sideX, sideY)
        };
    }
}
