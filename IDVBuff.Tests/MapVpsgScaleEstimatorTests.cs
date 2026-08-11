using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace IDVBuff.Tests;

public sealed class MapVpsgScaleEstimatorTests
{
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
        Assert.True(estimate.Evidence.UniqueMatches >= 12);
        Assert.True(estimate.Evidence.PairVotes >= 24);
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
