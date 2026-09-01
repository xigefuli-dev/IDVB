using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void LocalAlignmentResearchB1fSamplesStartInExpectedBasins()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB",
            "AlignmentResearch",
            "sessions",
            "2026-08-29_045917--d7f4c879");
        if (!Directory.Exists(root))
            return;

        AssertResearchBasin(
            root,
            "1fefccc8",
            "1fefccc837cd490d8f757d86e5e4c274",
            0.57d,
            0.08d);
        AssertResearchBasin(
            root,
            "104df586",
            "104df586a2b04687ba059b1615e469dc",
            0.44d,
            0.08d);
    }

    private static void AssertResearchBasin(
        string researchRoot,
        string researchMapDirectory,
        string mapDirectory,
        double expectedScale,
        double tolerance)
    {
        var sampleDirectory = Directory.GetDirectories(
                Path.Combine(researchRoot, researchMapDirectory, "b1f"),
                "*-ok-high-*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .First();
        var livePath = Path.Combine(sampleDirectory, "viewport.png");
        var referencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB",
            "Maps",
            mapDirectory,
            "floor-b1f-recognition.png");
        using var liveImage = Cv2.ImRead(livePath, ImreadModes.Unchanged);
        using var referenceImage = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        Assert.False(liveImage.Empty());
        Assert.False(referenceImage.Empty());
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new LowStructureConfig { MinimumEdgePixels = 20, MinimumSpanPixels = 12 });
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

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var selection = MapStructureLowScaleSelector.Analyze(live, reference, tuning);
        timer.Stop();

        Assert.NotEmpty(selection.Scales);
        Assert.InRange(
            Math.Abs(selection.Scales[0] - expectedScale),
            0d,
            tolerance);
        Assert.False(selection.Scales.Take(3).All(scale => scale >= 1.36d));
        Assert.InRange(timer.Elapsed.TotalMilliseconds, 0d, 800d);
    }

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

    [Fact]
    public void AmbiguousSelectorSpendsExactBudgetAcrossThreeBasins()
    {
        var selected = MapStructureLowScaleSelector.SelectExactCandidates(
            0.57d,
            [0.568d, 0.57d, 0.572d],
            [0.57d, 1.36d, 1.70d, 0.64d],
            ambiguous: true);

        Assert.Equal(3, selected.Count);
        Assert.Equal([0.57d, 1.36d, 1.70d], selected);
        Assert.Equal(3, selected.Distinct().Count());
    }

    [Fact]
    public void ConfidentSelectorSpendsExactBudgetOnlyOnFineNeighbours()
    {
        var selected = MapStructureLowScaleSelector.SelectExactCandidates(
            0.57d,
            [0.568d, 0.57d, 0.572d],
            [0.57d, 1.36d, 1.70d],
            ambiguous: false);

        Assert.Equal([0.57d, 0.568d, 0.572d], selected);
        Assert.Equal(3, selected.Count);
    }

    [Theory]
    [MemberData(
        nameof(DisplayTestMatrix.All),
        MemberType = typeof(DisplayTestMatrix))]
    public void SparseScaleSelectorProducesARegistrationCandidateAcrossEveryDisplayConfiguration(
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
        Assert.Contains(
            ranked,
            scale => Math.Abs((scale / plantedScale) - 1d) <= 0.015d);
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
        var relativeScaleError = Math.Abs(
            (result.Transform.ScaleX / plantedScale) - 1d);
        Assert.True(
            relativeScaleError <= 0.015d,
            $"scaleError={relativeScaleError:F6}; selected={result.Transform.ScaleX:F9}; "
            + $"ranked={string.Join(",", rankedScales.Select(scale => scale.ToString("F9")))}");
        Assert.InRange(result.SearchMilliseconds, 0d, 700d);
    }
}
