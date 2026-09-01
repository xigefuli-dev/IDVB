using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class LowStructureAlignmentValidatorRegressionTests
{
    [Fact]
    public void LatestDiagnosticMisalignmentIsRejected()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var candidate = new MapStructureCandidate
        {
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 2.8715d,
            EdgeCoverage = 0.5676d,
            OccupancyCoverage = 0.7044d,
            ReferenceCoverage = 0.6555d,
            ProjectionCorrelation = 0.6144d,
            ConsistentPartitions = 4
        };

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning));
    }

    [Fact]
    public void LatestClearCorridorFrameIsAccepted()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var candidate = new MapStructureCandidate
        {
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 0.901d,
            EdgeCoverage = 0.928d,
            OccupancyCoverage = 0.821d,
            ReferenceCoverage = 0.498d,
            ProjectionCorrelation = 0.662d,
            ConsistentPartitions = 4
        };

        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning));
    }

    [Fact]
    public void PerpendicularAxisMisalignmentCannotBeHiddenByAverageProjection()
    {
        using var query = Mat.Zeros(40, 40, MatType.CV_8UC1).ToMat();
        using var reference = Mat.Zeros(40, 40, MatType.CV_8UC1).ToMat();
        Cv2.Line(query, new Point(4, 10), new Point(35, 10), Scalar.White);
        Cv2.Line(query, new Point(8, 4), new Point(8, 35), Scalar.White);
        Cv2.Line(reference, new Point(4, 10), new Point(35, 10), Scalar.White);
        Cv2.Line(reference, new Point(28, 4), new Point(28, 35), Scalar.White);

        Assert.True(MapStructureProjectionScorer.Score(
            query, reference, 0, 0) < 0.60d);
    }
}
