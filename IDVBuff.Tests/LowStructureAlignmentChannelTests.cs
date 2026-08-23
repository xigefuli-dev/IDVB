using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class LowStructureAlignmentChannelTests
{
    [Fact]
    public void MarkerKeysNormalizeAndRetainUnknownLegalKeys()
    {
        var normalized = MapFloorMarkerRules.Normalize(
            [" LOW_STRUCTURE ", "future_marker", "low_structure", "Future_Marker"]);

        Assert.Equal(["future_marker", "low_structure"], normalized);
        Assert.True(MapFloorMarkerRules.Has(normalized, "LOW_STRUCTURE"));
        Assert.False(MapFloorMarkerRules.IsValid("future marker"));
    }

    [Fact]
    public void ChannelResolutionUsesOnlyTheRequestedFloor()
    {
        var map = new MapRecord
        {
            Floors =
            [
                new FloorDefinition
                {
                    Key = "1f",
                    SortOrder = 1,
                    MarkerKeys = [MapFloorMarkerRules.LowStructure]
                },
                new FloorDefinition
                {
                    Key = "2f",
                    SortOrder = 2,
                    MarkerKeys = ["future_marker"]
                }
            ]
        };

        Assert.Equal(
            MapAlignmentChannel.LowStructure,
            MapAlignmentChannelRegistry.Resolve(map, "1f").Channel);
        Assert.Equal(
            MapAlignmentChannel.Standard,
            MapAlignmentChannelRegistry.Resolve(map, "2f").Channel);
    }

    [Fact]
    public void LowStructureTuningDoesNotInheritOrMutateStandardTuning()
    {
        var standard = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 9d,
            DistanceClipPixels = 31d,
            MinimumEdgePixels = 777,
            ScaleSearchStep = 0.025d
        };
        var low = MapAlignmentChannelRegistry.CreateLowStructure(
            new LowStructureConfig());

        Assert.Equal(3.0d, standard.MaximumChamferPixels);
        Assert.Equal(0.40d, standard.MinimumEdgeCoverage);
        Assert.Equal(3.0d, low.MaximumChamferPixels);
        Assert.Equal(12d, low.DistanceClipPixels);
        Assert.Equal(90, low.MinimumEdgePixels);
        Assert.Equal(0.01d, low.ScaleSearchStep);
        Assert.Equal(0.30d, low.MinimumEdgeCoverage);
        Assert.Equal(0.25d, low.MinimumOccupancyCoverage);
        Assert.Equal(1, low.MinimumConsistentPartitions);
        Assert.False(low.EnforceTimeBudget);
        Assert.Equal(5, low.FastCoarseTopK);
        Assert.NotEqual(standard.CacheFingerprint, low.CacheFingerprint);
    }

    [Fact]
    public void DefaultLowStructureSearchExtractsDescriptorsForContentScaleBootstrap()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();

        Assert.False(tuning.EnableFeatureVoting);
        Assert.Equal(
            MapStructurePreprocessingProfile.EdgesAndFeatures,
            MapCvAlignmentService.ResolveLiveStructurePreprocessingProfile(
                MapScaleSearchPolicy.Search,
                isTracking: false,
                tuning));
        Assert.Equal(
            MapStructurePreprocessingProfile.EdgesOnly,
            MapCvAlignmentService.ResolveLiveStructurePreprocessingProfile(
                MapScaleSearchPolicy.Fixed,
                isTracking: false,
                tuning));
        Assert.Equal(
            MapStructurePreprocessingProfile.EdgesOnly,
            MapCvAlignmentService.ResolveLiveStructurePreprocessingProfile(
                MapScaleSearchPolicy.Search,
                isTracking: true,
                tuning));
    }

    [Theory]
    [InlineData(MapAlignmentChannel.LowStructure, true, true, 0.48d, 0.60d, true)]
    [InlineData(MapAlignmentChannel.Standard, true, true, 0.48d, 0.60d, false)]
    [InlineData(MapAlignmentChannel.LowStructure, false, true, 0.90d, 0.60d, false)]
    [InlineData(MapAlignmentChannel.LowStructure, true, false, 0.90d, 0.60d, false)]
    [InlineData(MapAlignmentChannel.LowStructure, true, true, double.NaN, 0.60d, false)]
    public void LowStructureHardGateAcceptanceIsNotRejectedByStandardConfidence(
        MapAlignmentChannel channel,
        bool accepted,
        bool hasTransform,
        double confidence,
        double minimumStandardConfidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.IsAcceptedStructureAlignment(
                channel,
                accepted,
                hasTransform,
                confidence,
                minimumStandardConfidence));
    }

    [Fact]
    public void CacheFingerprintCoversEveryRuntimeTuningField()
    {
        var baseline = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion
        };
        var changes = new (string Name, Action<MapStructureRegistrationTuning> Apply)[]
        {
            ("feature", tuning => tuning.EnableFeatureVoting = !tuning.EnableFeatureVoting),
            ("distance", tuning => tuning.DistanceClipPixels += 1d),
            ("edges", tuning => tuning.MinimumEdgePixels += 1),
            ("scale", tuning => tuning.ScaleSearchRadius += 0.01d),
            ("tracking", tuning => tuning.TrackingScaleSearchRadius += 0.01d),
            ("refinement", tuning => tuning.RefinementWorsenTolerance += 0.01d),
            ("generation", tuning => tuning.Generation.CannyLowThreshold += 1d)
        };

        foreach (var change in changes)
        {
            var modified = baseline.Clone();
            change.Apply(modified);
            Assert.True(
                baseline.CacheFingerprint != modified.CacheFingerprint,
                change.Name);
        }
    }

    [Fact]
    public void ColdLowStructureScalesAreLogSpacedAcrossAllThirteenHypotheses()
    {
        var scales = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
            0.40d,
            1.60d,
            13);

        Assert.Equal(13, scales.Count);
        Assert.Equal(0.40d, scales[0], 3);
        Assert.Equal(1.60d, scales[^1], 3);
        Assert.True(scales.SequenceEqual(scales.OrderBy(value => value)));
        Assert.InRange(scales[1], 0.44d, 0.46d);
        Assert.InRange(scales[8], 1.00d, 1.02d);
    }

    [Fact]
    public void TrustedSameFloorScaleReplacesOneHypothesisWithoutAddingWork()
    {
        const double trustedScale = 0.557d;
        var scales = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
            0.40d,
            1.60d,
            13,
            preferredScale: trustedScale);

        Assert.Equal(13, scales.Count);
        Assert.Equal(13, scales.Distinct().Count());
        Assert.Contains(trustedScale, scales);
        Assert.Equal(trustedScale, scales[0], 6);
        Assert.Contains(scales, scale => Math.Abs(scale - 0.40d) < 0.001d);
        Assert.Contains(scales, scale => Math.Abs(scale - 1.60d) < 0.001d);
    }

    [Fact]
    public void FixedScalePolicyCreatesExactlyOneScaleHypothesis()
    {
        var scales = MapStructureScaleSearch.BuildScaleHypotheses(
            0.4671000230552575d,
            allowScaleSearch: false,
            scaleSearchRadius: 0.60d,
            scaleSearchStep: 0.01d);

        Assert.Equal(0.4671000230552575d, Assert.Single(scales), 12);
    }

    [Theory]
    [InlineData(MapAlignmentChannel.LowStructure, false, false)]
    [InlineData(MapAlignmentChannel.LowStructure, true, false)]
    [InlineData(MapAlignmentChannel.Standard, false, true)]
    [InlineData(MapAlignmentChannel.Standard, true, false)]
    public void LowStructureNeverChangesContentDomainForSubOneScale(
        MapAlignmentChannel channel,
        bool restricted,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapStructureRegistrar.ShouldUseReciprocalScale(
                channel,
                baselineScale: 0.4671d,
                restrictSearchToLockedTransform: restricted));
    }

    [Fact]
    public void CandidateRankingFindsValidAnswerBeyondInvalidDiagnosticCutoff()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new LowStructureConfig { TopCandidateCount = 3 });
        var invalidCandidates = Enumerable.Range(0, 4)
            .Select(index => new MapStructureCandidate
            {
                Scale = 0.40d + (index * 0.01d),
                CompositeCost = 0.10d + (index * 0.01d),
                ChamferPixels = 3.01d,
                EdgeCoverage = 0.90d,
                OccupancyCoverage = 0.90d,
                ConsistentPartitions = 4
            })
            .ToArray();
        var valid = new MapStructureCandidate
        {
            Scale = 0.46d,
            CompositeCost = 0.20d,
            ChamferPixels = 2.0d,
            EdgeCoverage = 0.80d,
            OccupancyCoverage = 0.75d,
            ConsistentPartitions = 3
        };
        var candidates = invalidCandidates.Append(valid).ToArray();

        var ranking = MapStructureCandidateCollector.RankCandidatesByValidity(
            candidates,
            tuning,
            new MapOverlayTransform { ScaleX = 1d, ScaleY = 1d },
            restrictedSearch: false);

        Assert.Equal(5, ranking.Ordered.Length);
        Assert.Equal(3, ranking.Diagnostic.Length);
        Assert.DoesNotContain(valid, ranking.Diagnostic);
        Assert.Same(valid, Assert.Single(ranking.Valid));
    }

    [Fact]
    public void StandardAndLowCacheKeysCannotAlias()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            Floors = [new FloorDefinition { Key = "basement", SortOrder = 1 }]
        };
        var resolution = new MapCacheResolutionSignature(1920, 1080, 1920, 1080);
        var standard = new MapStructureRegistrationTuning();
        var low = MapAlignmentChannelRegistry.CreateLowStructure();

        var standardKey = MapFeatureCacheRules.CreateKey(
            map,
            "basement",
            resolution,
            MapAlignmentChannel.Standard,
            standard.CacheFingerprint);
        var lowKey = MapFeatureCacheRules.CreateKey(
            map,
            "basement",
            resolution,
            MapAlignmentChannel.LowStructure,
            low.CacheFingerprint);

        Assert.NotEqual(standardKey, lowKey);
        Assert.Equal("standard", standardKey.Channel);
        Assert.Equal("low_structure", lowKey.Channel);
    }
}
