using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void FineScaleGridFillsTheSparseCoarseBasinAtHalfPercentSteps()
    {
        var coarse = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
            0.40d,
            1.60d,
            13)
            .OrderBy(scale => scale)
            .ToArray();
        var winnerIndex = Array.FindIndex(
            coarse,
            scale => scale > 0.68d && scale < 0.72d);

        var fine = MapStructureLowScaleSelector.BuildFineScaleGrid(
            coarse,
            winnerIndex,
            maximumRelativeStep: 0.005d)
            .OrderBy(scale => scale)
            .ToArray();

        Assert.NotEmpty(fine);
        Assert.True(fine.Min(scale => Math.Abs((scale / 0.665d) - 1d)) < 0.006d);
        Assert.All(
            fine.Zip(fine.Skip(1)),
            pair => Assert.True(Math.Log(pair.Second / pair.First) <= 0.00501d));
    }

    [Theory]
    [MemberData(
        nameof(DisplayTestMatrix.All),
        MemberType = typeof(DisplayTestMatrix))]
    public void SparseScaleSelectorConvergesAcrossEveryDisplayConfiguration(
        string displayName,
        int pixelWidth,
        int pixelHeight,
        int scalePercent,
        uint dpi)
    {
        _ = (displayName, pixelHeight, scalePercent, dpi);
        using var referenceImage = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(referenceImage, crop);
        var plantedScale = pixelWidth / 2560d;
        using var liveImage = new Mat();
        Cv2.Resize(
            source,
            liveImage,
            new Size(
                (int)Math.Round(source.Width * plantedScale),
                (int)Math.Round(source.Height * plantedScale)),
            interpolation: InterpolationFlags.Nearest);
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new LowStructureConfig
            {
                MinimumEdgePixels = 20,
                MinimumSpanPixels = 12
            });
        var preprocessor = new MapStructurePreprocessor();
        using var reference = preprocessor.ProcessReference(
            referenceImage,
            null,
            tuning.Generation);
        using var live = preprocessor.ProcessLiveRoi(
            liveImage,
            null,
            null,
            generateVisibleMask: true,
            profile: MapStructurePreprocessingProfile.EdgesOnly,
            generationTuning: tuning.Generation);

        var ranked = MapStructureLowScaleSelector.Rank(
            live,
            reference,
            tuning);

        Assert.NotEmpty(ranked);
        Assert.InRange(
            Math.Abs((ranked[0] / plantedScale) - 1d),
            0d,
            0.015d);
    }

    [Theory]
    [InlineData(0.48d)]
    [InlineData(0.665d)]
    [InlineData(0.92d)]
    [InlineData(1.28d)]
    public void SparseExactRegistrationLocksFineScaleWithoutGridGhosting(
        double plantedScale)
    {
        using var referenceImage = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(referenceImage, crop);
        using var liveImage = new Mat();
        Cv2.Resize(
            source,
            liveImage,
            new Size(
                (int)Math.Round(source.Width * plantedScale),
                (int)Math.Round(source.Height * plantedScale)),
            interpolation: InterpolationFlags.Nearest);
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new LowStructureConfig
            {
                MaximumChamferPixels = 4d,
                MinimumEdgeCoverage = 0.45d,
                MinimumOccupancyCoverage = 0.20d,
                MinimumCandidateMargin = 0.02d,
                MinimumConsistentPartitions = 2,
                MinimumEdgePixels = 20,
                MinimumSpanPixels = 12
            });
        var preprocessor = new MapStructurePreprocessor();
        using var reference = preprocessor.ProcessReference(
            referenceImage,
            null,
            tuning.Generation);
        using var live = preprocessor.ProcessLiveRoi(
            liveImage,
            null,
            null,
            generateVisibleMask: true,
            profile: MapStructurePreprocessingProfile.EdgesOnly,
            generationTuning: tuning.Generation);
        var rankedScales = MapStructureLowScaleSelector.Rank(
            live,
            reference,
            tuning);
        var plan = LowStructureAlignmentPlan.SparseCoarseSeed(
            rankedScales,
            LowStructureAlignmentPlan.CreateConfig(tuning));
        var viewport = new MapScreenRect(
            600d,
            300d,
            liveImage.Width,
            liveImage.Height);
        var registrar = new MapStructureRegistrar(preprocessor);

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = referenceImage,
            LiveRoi = liveImage,
            PreparedReference = reference,
            PreparedLive = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(referenceImage),
            Tuning = tuning,
            Channel = MapAlignmentChannel.LowStructure,
            ScaleSearchPolicy = MapScaleSearchPolicy.Search,
            LowStructurePlan = plan
        });

        Assert.True(
            result.Accepted,
            $"{result.FailureReason}; planted={plantedScale:F4}; "
            + CandidateMetrics(result));
        Assert.NotNull(result.Transform);
        Assert.InRange(
            Math.Abs((result.Transform.ScaleX / plantedScale) - 1d),
            0d,
            0.015d);
        Assert.InRange(result.SearchMilliseconds, 0d, 700d);
    }
}
