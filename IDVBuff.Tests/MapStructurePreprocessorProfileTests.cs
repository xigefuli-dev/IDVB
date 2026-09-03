using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructurePreprocessorProfileTests
{
    [Fact]
    public void EdgePairScaleHintTracksLiveToReferenceScale()
    {
        using var referenceEdges = new Mat(180, 240, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(referenceEdges, new Point(20, 24), new Point(210, 24), Scalar.White, 2);
        Cv2.Line(referenceEdges, new Point(32, 30), new Point(32, 150), Scalar.White, 2);
        Cv2.Rectangle(referenceEdges, new Rect(72, 58, 76, 62), Scalar.White, 2);
        Cv2.Line(referenceEdges, new Point(150, 42), new Point(205, 150), Scalar.White, 2);
        using var liveEdges = new Mat();
        Cv2.Resize(
            referenceEdges,
            liveEdges,
            new Size(288, 216),
            interpolation: InterpolationFlags.Nearest);
        using var reference = CreateEdgeFeatures(referenceEdges);
        using var live = CreateEdgeFeatures(liveEdges);

        Assert.True(
            MapStructureScaleHintEstimator.TryEstimate(
                reference,
                live,
                0.30d,
                1.70d,
                out var hint));
        Assert.InRange(hint.Scale, 1.12d, 1.28d);
        Assert.InRange(hint.Confidence, 0d, 0.98d);
    }

    private static MapStructureFeatures CreateEdgeFeatures(Mat edges) =>
        new(
            new Mat(edges.Size(), MatType.CV_8UC1, Scalar.Black),
            edges.Clone(),
            edges.Clone());

    [Fact]
    public void EdgesOnlySkipsDescriptorsWithoutChangingStructureCoordinates()
    {
        using var source = CreateFeatureRichImage();
        var preprocessor = new MapStructurePreprocessor();

        using var edgesOnly = preprocessor.ProcessLiveRoiDiagnostic(
            source,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            out var edgesOnlyTiming,
            profile: MapStructurePreprocessingProfile.EdgesOnly);
        using var withFeatures = preprocessor.ProcessLiveRoiDiagnostic(
            source,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            out var withFeaturesTiming,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures);

        Assert.Equal(
            MapStructurePreprocessingProfile.EdgesOnly,
            edgesOnlyTiming.Profile);
        Assert.True(edgesOnlyTiming.DescriptorExtractionSkipped);
        Assert.Equal(0d, edgesOnlyTiming.FeaturesMs);
        Assert.Empty(edgesOnly.KeyPoints);
        Assert.True(edgesOnly.Descriptors.Empty());
        Assert.Equal(0, edgesOnly.Descriptors.Rows);

        Assert.Equal(
            MapStructurePreprocessingProfile.EdgesAndFeatures,
            withFeaturesTiming.Profile);
        Assert.False(withFeaturesTiming.DescriptorExtractionSkipped);
        Assert.NotEmpty(withFeatures.KeyPoints);
        Assert.False(withFeatures.Descriptors.Empty());
        Assert.Equal(withFeatures.KeyPoints.Length, withFeatures.Descriptors.Rows);

        Assert.Equal(withFeatures.Edges.Size(), edgesOnly.Edges.Size());
        Assert.Equal(
            0d,
            Cv2.Norm(withFeatures.Edges, edgesOnly.Edges, NormTypes.INF));
        Assert.Equal(
            0d,
            Cv2.Norm(
                withFeatures.StructureMask,
                edgesOnly.StructureMask,
                NormTypes.INF));
    }

    [Fact]
    public void DescriptorProfileCompatibilityIsOneWay()
    {
        Assert.True(
            MapStructurePreprocessingProfile.EdgesAndFeatures.CanSatisfy(
                MapStructurePreprocessingProfile.EdgesOnly));
        Assert.True(
            MapStructurePreprocessingProfile.EdgesOnly.CanSatisfy(
                MapStructurePreprocessingProfile.EdgesOnly));
        Assert.False(
            MapStructurePreprocessingProfile.EdgesOnly.CanSatisfy(
                MapStructurePreprocessingProfile.EdgesAndFeatures));
    }

    [Fact]
    public void EdgeOnlyBaseCanUpgradeWithoutRepeatingStructureExtraction()
    {
        using var source = CreateFeatureRichImage();
        var preprocessor = new MapStructurePreprocessor();
        using var edgesOnly = preprocessor.ProcessLiveRoiDiagnostic(
            source,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            out var baseTiming,
            profile: MapStructurePreprocessingProfile.EdgesOnly);

        using var upgraded =
            MapStructurePreprocessor.UpgradeLiveRoiWithDescriptors(
                edgesOnly,
                out var upgradeTiming);

        Assert.Equal(
            MapStructurePreprocessingProfile.EdgesAndFeatures,
            upgradeTiming.Profile);
        Assert.False(upgradeTiming.DescriptorExtractionSkipped);
        Assert.NotEmpty(upgraded.KeyPoints);
        Assert.Equal(upgraded.KeyPoints.Length, upgraded.Descriptors.Rows);
        Assert.Equal(baseTiming.ClaheBlurMs, upgradeTiming.ClaheBlurMs);
        Assert.Equal(baseTiming.NuisanceMaskMs, upgradeTiming.NuisanceMaskMs);
        Assert.Equal(baseTiming.StructureMs, upgradeTiming.StructureMs);
        Assert.Equal(baseTiming.EdgesMs, upgradeTiming.EdgesMs);
        Assert.Equal(baseTiming.PyramidMs, upgradeTiming.PyramidMs);
        Assert.Equal(
            0d,
            Cv2.Norm(edgesOnly.Edges, upgraded.Edges, NormTypes.INF));
        Assert.Equal(
            0d,
            Cv2.Norm(
                edgesOnly.StructureMask,
                upgraded.StructureMask,
                NormTypes.INF));
    }

    private static Mat CreateFeatureRichImage()
    {
        var image = new Mat(
            new Size(360, 280),
            MatType.CV_8UC3,
            Scalar.Black);
        for (var x = 36; x <= 324; x += 36)
        {
            Cv2.Line(
                image,
                new Point(x, 28),
                new Point(x, 252),
                Scalar.White,
                5);
        }
        for (var y = 28; y <= 252; y += 32)
        {
            Cv2.Line(
                image,
                new Point(36, y),
                new Point(324, y),
                Scalar.White,
                5);
        }
        Cv2.Circle(image, new Point(180, 140), 58, new Scalar(90, 220, 255), 9);
        Cv2.Rectangle(
            image,
            new Rect(82, 72, 74, 58),
            new Scalar(255, 130, 70),
            -1);
        Cv2.PutText(
            image,
            "IDVB",
            new Point(102, 222),
            HersheyFonts.HersheySimplex,
            1.25d,
            new Scalar(120, 255, 100),
            4);
        return image;
    }
}
