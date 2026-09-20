using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace IDVBuff.Tests;

public sealed class MapVpsgScaleEstimatorTests
{
    [Theory]
    [InlineData(13, 0.84d, false)]
    [InlineData(13, 0.85d, true)]
    [InlineData(14, 0.70d, true)]
    public void HighConfidenceCanBypassPairVoteTarget(
        int pairVotes,
        double confidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapVpsgScaleEstimator.HasSufficientPairEvidence(
                pairVotes,
                confidence));
    }

    [Fact]
    public void AkazeScaleGraphRecoversScaleWithoutTrustingWrongFloorPrior()
    {
        const double expectedScale = 1.3375d;
        var referencePoints = Enumerable.Range(0, 24)
            .Select(index => new KeyPoint(
                90f + ((index % 6) * 150f),
                80f + ((index / 6) * 140f),
                12f,
                response: 1f))
            .ToArray();
        var livePoints = referencePoints
            .Select(point => new KeyPoint(
                (float)((point.Pt.X * expectedScale) + 73d),
                (float)((point.Pt.Y * expectedScale) + 41d),
                point.Size,
                response: point.Response))
            .ToArray();
        var descriptors = CreateUniqueDescriptors(referencePoints.Length);
        using var reference = CreateFeatures(
            new Size(1000, 650),
            referencePoints,
            descriptors);
        using var live = CreateFeatures(
            new Size(1421, 1249),
            livePoints,
            descriptors);
        var graph = MapVpsgScaleGraphCache.Build(
            reference.Edges.Size(),
            referencePoints);

        var succeeded = new MapVpsgScaleEstimator().TryEstimate(
            reference,
            live,
            graph,
            priorScale: 1.068d,
            out var estimate,
            out var rejection);

        Assert.True(succeeded, rejection);
        Assert.NotNull(estimate);
        Assert.InRange(estimate!.Scale, expectedScale - 0.002d, expectedScale + 0.002d);
        Assert.True(estimate.Evidence.UniqueMatches
            >= MapVpsgScaleEstimator.MinimumUniqueMatches);
        Assert.True(estimate.Evidence.PairVotes
            >= MapVpsgScaleEstimator.MinimumPairVotes);
        Assert.InRange(estimate.Evidence.ResidualPixels, 0d, 0.2d);
    }

    [Fact]
    public void AkazeScaleGraphPriorScaleDoesNotGateScale()
    {
        const double expectedScale = 1.3375d;
        var referencePoints = Enumerable.Range(0, 24)
            .Select(index => new KeyPoint(
                90f + ((index % 6) * 150f),
                80f + ((index / 6) * 140f),
                12f,
                response: 1f))
            .ToArray();
        var livePoints = referencePoints
            .Select(point => new KeyPoint(
                (float)((point.Pt.X * expectedScale) + 73d),
                (float)((point.Pt.Y * expectedScale) + 41d),
                point.Size,
                response: point.Response))
            .ToArray();
        var descriptors = CreateUniqueDescriptors(referencePoints.Length);
        using var reference = CreateFeatures(
            new Size(1000, 650),
            referencePoints,
            descriptors);
        using var live = CreateFeatures(
            new Size(1421, 1249),
            livePoints,
            descriptors);
        var graph = MapVpsgScaleGraphCache.Build(
            reference.Edges.Size(),
            referencePoints);

        var estimator = new MapVpsgScaleEstimator();
        Assert.True(estimator.TryEstimate(
            reference,
            live,
            graph,
            priorScale: 1.0d,
            out var lowPrior,
            out var lowRejection), lowRejection);
        Assert.True(estimator.TryEstimate(
            reference,
            live,
            graph,
            priorScale: 2.0d,
            out var highPrior,
            out var highRejection), highRejection);

        Assert.InRange(
            lowPrior!.Scale,
            expectedScale - 0.002d,
            expectedScale + 0.002d);
        Assert.InRange(
            highPrior!.Scale,
            expectedScale - 0.002d,
            expectedScale + 0.002d);
    }

    [Fact]
    public void AkazeScaleComesFromMatchedContentInsteadOfCanvasDimensions()
    {
        const double expectedScale = 0.46d;
        var referencePoints = Enumerable.Range(0, 24)
            .Select(index => new KeyPoint(
                90f + ((index % 6) * 150f),
                80f + ((index / 6) * 140f),
                12f,
                response: 1f))
            .ToArray();
        var livePoints = referencePoints
            .Select(point => new KeyPoint(
                (float)((point.Pt.X * expectedScale) + 24d),
                (float)((point.Pt.Y * expectedScale) + 31d),
                point.Size,
                response: point.Response))
            .ToArray();
        var descriptors = CreateUniqueDescriptors(referencePoints.Length);
        using var reference = CreateFeatures(
            new Size(1200, 900),
            referencePoints,
            descriptors);
        using var compactCanvas = CreateFeatures(
            new Size(500, 400),
            livePoints,
            descriptors);
        using var oversizedCanvas = CreateFeatures(
            new Size(1700, 1300),
            livePoints,
            descriptors);
        var graph = MapVpsgScaleGraphCache.Build(
            reference.Edges.Size(),
            referencePoints);
        var estimator = new MapVpsgScaleEstimator();

        Assert.True(estimator.TryEstimate(
            reference,
            compactCanvas,
            graph,
            priorScale: 1d,
            out var compact,
            out var compactRejection), compactRejection);
        Assert.True(estimator.TryEstimate(
            reference,
            oversizedCanvas,
            graph,
            priorScale: 1d,
            out var oversized,
            out var oversizedRejection), oversizedRejection);

        Assert.InRange(compact!.Scale, 0.458d, 0.462d);
        Assert.InRange(oversized!.Scale, 0.458d, 0.462d);
        Assert.Equal(compact.Scale, oversized.Scale, 9);
    }

    [Fact]
    public void PreprocessedImageContentRecoversSubHalfScale()
    {
        const double expectedScale = 0.46d;
        using var referenceImage = BuildFeatureRichImage(1000, 700);
        using var liveImage = new Mat();
        Cv2.Resize(
            referenceImage,
            liveImage,
            new Size(
                (int)Math.Round(referenceImage.Width * expectedScale),
                (int)Math.Round(referenceImage.Height * expectedScale)),
            interpolation: InterpolationFlags.Area);
        var preprocessor = new MapStructurePreprocessor();
        using var reference = preprocessor.Process(referenceImage);
        using var live = preprocessor.ProcessLiveRoiAkaze(liveImage);
        var graph = MapVpsgScaleGraphCache.Build(
            reference.Edges.Size(),
            reference.KeyPoints);

        var succeeded = new MapVpsgScaleEstimator().TryEstimate(
            reference,
            live,
            graph,
            priorScale: 1d,
            out var estimate,
            out var rejection);

        Assert.True(succeeded, rejection);
        Assert.InRange(reference.KeyPoints.Length, 10, int.MaxValue);
        Assert.InRange(live.KeyPoints.Length, 10, int.MaxValue);
        Assert.InRange(estimate!.Scale, 0.44d, 0.48d);
    }

    [Fact]
    public void AkazeScaleGraphRejectsInsufficientMatches()
    {
        var points = Enumerable.Range(0, 8)
            .Select(index => new KeyPoint(100f + (index * 90f), 100f, 12f))
            .ToArray();
        var descriptors = CreateUniqueDescriptors(points.Length);
        using var reference = CreateFeatures(
            new Size(1000, 650),
            points,
            descriptors);
        using var live = CreateFeatures(
            new Size(1421, 1249),
            points,
            descriptors);
        var graph = MapVpsgScaleGraphCache.Build(reference.Edges.Size(), points);

        Assert.False(new MapVpsgScaleEstimator().TryEstimate(
            reference,
            live,
            graph,
            1d,
            out _,
            out var rejection));
        Assert.Contains("reciprocal AKAZE matches", rejection);
    }

    [Fact]
    public void AkazeScaleGraphRejectsDescriptorWidthMismatchWithoutOpenCvException()
    {
        var points = Enumerable.Range(0, 16)
            .Select(index => new KeyPoint(
                50f + ((index % 4) * 100f),
                50f + ((index / 4) * 100f),
                12f))
            .ToArray();
        using var reference = CreateFeatures(
            new Size(500, 500),
            points,
            CreateUniqueDescriptors(points.Length, 61));
        using var live = CreateFeatures(
            new Size(500, 500),
            points,
            CreateUniqueDescriptors(points.Length, 32));
        var graph = MapVpsgScaleGraphCache.Build(reference.Edges.Size(), points);

        var succeeded = new MapVpsgScaleEstimator().TryEstimate(
            reference,
            live,
            graph,
            1d,
            out _,
            out var rejection);

        Assert.False(succeeded);
        Assert.Contains("incompatible AKAZE descriptors", rejection);
    }

    private static byte[] CreateUniqueDescriptors(
        int count,
        int descriptorSize = 61)
    {
        var values = new byte[count * descriptorSize];
        for (var row = 0; row < count; row++)
        {
            var random = new Random(0x51A7 + row);
            random.NextBytes(values.AsSpan(
                row * descriptorSize,
                descriptorSize));
        }
        return values;
    }

    private static Mat BuildFeatureRichImage(int width, int height)
    {
        var image = new Mat(
            new Size(width, height),
            MatType.CV_8UC3,
            new Scalar(18d, 18d, 18d));
        var random = new Random(0x1D0B);
        for (var index = 0; index < 80; index++)
        {
            var center = new Point(
                random.Next(35, width - 35),
                random.Next(35, height - 35));
            var gray = random.Next(90, 245);
            var color = new Scalar(gray, gray, gray);
            if ((index & 1) == 0)
            {
                Cv2.Circle(
                    image,
                    center,
                    random.Next(6, 22),
                    color,
                    random.Next(2, 6));
            }
            else
            {
                var halfWidth = random.Next(7, 24);
                var halfHeight = random.Next(7, 24);
                Cv2.Rectangle(
                    image,
                    new Rect(
                        center.X - halfWidth,
                        center.Y - halfHeight,
                        halfWidth * 2,
                        halfHeight * 2),
                    color,
                    random.Next(2, 6));
            }
            Cv2.Line(
                image,
                center,
                new Point(
                    Math.Clamp(center.X + random.Next(-45, 46), 0, width - 1),
                    Math.Clamp(center.Y + random.Next(-45, 46), 0, height - 1)),
                color,
                2);
        }
        return image;
    }

    private static MapStructureFeatures CreateFeatures(
        Size size,
        KeyPoint[] keyPoints,
        byte[] descriptorValues)
    {
        var nuisance = Mat.Zeros(size, MatType.CV_8UC1).ToMat();
        var structure = Mat.Zeros(size, MatType.CV_8UC1).ToMat();
        var edges = Mat.Zeros(size, MatType.CV_8UC1).ToMat();
        var descriptorSize = descriptorValues.Length / keyPoints.Length;
        var descriptors = new Mat(
            keyPoints.Length,
            descriptorSize,
            MatType.CV_8UC1);
        Marshal.Copy(
            descriptorValues,
            0,
            descriptors.Data,
            descriptorValues.Length);
        return new MapStructureFeatures(
            nuisance,
            structure,
            edges,
            keyPoints: keyPoints,
            descriptors: descriptors);
    }
}
