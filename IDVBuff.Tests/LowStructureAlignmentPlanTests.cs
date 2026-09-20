using IDVBuff.Features.Maps;
using IDVBuff.Core.Models;

namespace IDVBuff.Tests;

public sealed partial class LowStructureAlignmentPlanTests
{
    [Fact]
    public void ShapeVotesClusterAndKeepAtMostThreeRankedProposals()
    {
        var proposals = LowStructureScaleProposalBuilder.Cluster(
            new[]
            {
                (Scale: 1.00d, Weight: 3d, IsAxisEvidence: true),
                (Scale: 1.01d, Weight: 3d, IsAxisEvidence: true),
                (Scale: 1.40d, Weight: 4d, IsAxisEvidence: false),
                (Scale: 0.70d, Weight: 1d, IsAxisEvidence: false),
                (Scale: 9.00d, Weight: 100d, IsAxisEvidence: false)
            },
            minimumScale: 0.30d,
            maximumScale: 1.70d,
            tolerance: 0.015d,
            maximumProposals: 3);

        Assert.NotEmpty(proposals);
        Assert.InRange(proposals.Count, 1, 3);
        Assert.InRange(proposals[0].Scale, 1.00d, 1.01d);
        Assert.True(proposals[0].AxisAgreement > 0.99d);
        Assert.DoesNotContain(proposals, proposal => proposal.Scale > 1.70d);
    }

    [Fact]
    public void ShapeSeedRefusesDirectAcceptanceWhenAxisVotesDisagree()
    {
        var config = new LowStructureConfig
        {
            ScaleConsistencyTolerance = 0.015d,
            MaximumScalesPerFrame = 3,
            TranslationTopK = 2
        };
        var evidence = new LowStructureShapeScaleEvidence(
            WidthScale: 1.00d,
            HeightScale: 1.03d,
            ComponentScale: 1.01d,
            LineSpacingScale: 1.01d);

        var plan = LowStructureAlignmentPlan.ShapeSeed(
            [new LowStructureScaleProposal(1.01d, 4, 0.5d, 0d, 2d)],
            evidence,
            config);

        Assert.False(plan.CanDirectAccept);
        Assert.InRange(plan.Scales.Count, 1, 3);
        Assert.Equal(2, plan.TranslationTopK);
        Assert.False(plan.UsesVpsg);
    }

    [Fact]
    public void RecoveryPlanCapsPerFrameButRetainsFullGridCount()
    {
        var config = new LowStructureConfig
        {
            MaximumScalesPerFrame = 9,
            TranslationTopK = 9,
            ColdPathBudgetMilliseconds = 900
        };
        var grid = Enumerable.Range(0, 10)
            .Select(index => 0.40d + index * 0.10d)
            .ToArray();

        var plan = LowStructureAlignmentPlan.IncrementalRecovery(
            grid,
            batch: 2,
            config);

        Assert.Equal(3, plan.Scales.Count);
        Assert.Equal(grid.Length, plan.RecoveryTotalScaleCount);
        Assert.Equal(2, plan.RecoveryBatch);
        Assert.Equal(2, plan.TranslationTopK);
        Assert.Equal(700, plan.BudgetMilliseconds);
        Assert.False(plan.CanDirectAccept);
    }

    [Fact]
    public void SparseCoarseSeedRequiresCrossScaleComparisonBeforeAcceptance()
    {
        var config = new LowStructureConfig
        {
            MaximumScalesPerFrame = 3,
            TranslationTopK = 2
        };

        var plan = LowStructureAlignmentPlan.SparseCoarseSeed(
            [1.40d, 1.00d, 0.72d, 0.50d],
            config);

        Assert.Equal(LowStructureAlignmentRoute.SparseCoarseSeed, plan.Route);
        Assert.Equal([1.40d, 1.00d, 0.72d], plan.Scales);
        Assert.False(plan.CanDirectAccept);
    }

    [Fact]
    public void SparseScaleIntegrityRejectsOversizedOverlayButAllowsNarrowValidBasin()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var isolated = ValidCandidate(1.42d);
        var isolatedResult = AcceptedResult(
            isolated,
            [isolated],
            referenceWidth: 1000,
            referenceHeight: 800);

        Assert.False(MapCvAlignmentService.HasLowStructureScaleBasinSupport(
            isolatedResult,
            tuning));
        Assert.False(MapCvAlignmentService.HasLowStructureScaleIntegrity(
            isolatedResult,
            new MapScreenRect(0d, 0d, 400d, 300d),
            tuning));

        var neighbor = ValidCandidate(1.27d);
        var supportedResult = AcceptedResult(
            isolated,
            [isolated, neighbor],
            referenceWidth: 1000,
            referenceHeight: 800);
        Assert.True(MapCvAlignmentService.HasLowStructureScaleBasinSupport(
            supportedResult,
            tuning));
        Assert.False(MapCvAlignmentService.HasLowStructureScaleIntegrity(
            supportedResult,
            new MapScreenRect(0d, 0d, 400d, 300d),
            tuning));

        var narrow = ValidCandidate(0.63d);
        var narrowResult = AcceptedResult(
            narrow,
            [narrow],
            referenceWidth: 1000,
            referenceHeight: 800);
        Assert.False(MapCvAlignmentService.HasLowStructureScaleBasinSupport(
            narrowResult,
            tuning));
        Assert.True(MapCvAlignmentService.HasLowStructureScaleIntegrity(
            narrowResult,
            new MapScreenRect(0d, 0d, 800d, 600d),
            tuning));

        static MapStructureCandidate ValidCandidate(double scale) => new()
        {
            Scale = scale,
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 1.5d,
            EdgeCoverage = 0.8d,
            OccupancyCoverage = 0.8d,
            ReferenceCoverage = 0.8d,
            ProjectionCorrelation = 0.75d,
            ConsistentPartitions = 2
        };

        static MapStructureRegistrationResult AcceptedResult(
            MapStructureCandidate selected,
            IReadOnlyList<MapStructureCandidate> candidates,
            int referenceWidth,
            int referenceHeight) => new()
        {
            Accepted = true,
            Transform = new MapOverlayTransform
            {
                ScaleX = selected.Scale,
                ScaleY = selected.Scale
            },
            Candidates = candidates,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight
        };
    }

    [Fact]
    public void RecoveryCursorUsesDisjointBatchesAndResetsWithOperation()
    {
        var cursor = new LowStructureRecoveryCursor();
        var grid = new[] { 0.40d, 0.50d, 0.60d, 0.70d, 0.80d };

        var first = cursor.TakeBatch("map|floor|operation-1", grid, 3);
        var second = cursor.TakeBatch("map|floor|operation-1", grid, 3);
        var nextOperation = cursor.TakeBatch("map|floor|operation-2", grid, 3);

        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(first, nextOperation);
        cursor.Reset();
        Assert.Equal(first, cursor.TakeBatch("map|floor|operation-1", grid, 3));
    }

    [Fact]
    public void LowChannelClampsTopKAndKeepsFeatureScaleEstimateOptIn()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(
            new LowStructureConfig
            {
                MaximumScalesPerFrame = 9,
                TranslationTopK = 9,
                EnableFeatureScaleEstimate = false
            });

        Assert.Equal(MapAlignmentChannel.LowStructure, tuning.Channel);
        Assert.Equal(3, tuning.LowStructureMaximumScalesPerFrame);
        Assert.Equal(2, tuning.LowStructureTranslationTopK);
        Assert.Equal(2, tuning.FastCoarseTopK);
        Assert.False(tuning.LowStructureEnableFeatureScaleEstimate);
        Assert.True(tuning.EnforceTimeBudget);
    }

    [Theory]
    [InlineData(LowStructureCacheTrustLevel.None, false)]
    [InlineData(LowStructureCacheTrustLevel.Provisional, false)]
    [InlineData(LowStructureCacheTrustLevel.Trusted, true)]
    [InlineData(LowStructureCacheTrustLevel.Isolated, false)]
    public void LowCacheTrustLevelControlsRuntimeCacheUse(
        LowStructureCacheTrustLevel level,
        bool expectedTrusted)
    {
        var entry = new MapFeatureCacheEntry
        {
            Key = new MapFeatureCacheKey(
                Guid.NewGuid(),
                "content",
                "1f",
                new MapCacheResolutionSignature(1920, 1080, 1600, 900),
                Channel: "low_structure"),
            Scale = new MapScaleCachePayload
            {
                UniformScale = 1d,
                Source = MapFeatureCacheSource.Recovery,
                SampleCount = 2,
                Confidence = 0.9d,
                UpdatedAt = DateTimeOffset.UtcNow,
                Validation = new MapScaleCacheValidationMetadata
                {
                    LowStructureTrustLevel = level,
                    SuccessfulValidationCount = 2,
                    LastValidatedAt = DateTimeOffset.UtcNow
                }
            }
        };

        Assert.Equal(expectedTrusted, MapFeatureCacheRules.IsCacheEntryTrusted(entry));
    }

    [Fact]
    public void LowValidatorUsesTwoDimensionalCoverageInsteadOfLossySoftSignals()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var baseline = new MapStructureCandidate
        {
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 2d,
            ReverseChamferPixels = 2d,
            EdgeCoverage = 0.75d,
            OccupancyCoverage = 0.25d,
            ReferenceCoverage = 0.50d,
            ProjectionCorrelation = 0.75d,
            ConsistentPartitions = 1
        };

        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(baseline, 0.08d, 0.08d, tuning));
        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                baseline with { ReverseChamferPixels = 3.01d },
                0.08d,
                0.08d,
                tuning));
        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                baseline with { ProjectionCorrelation = 0.74d },
                0.08d,
                0.08d,
                tuning));
        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                baseline with { ReferenceCoverage = 0.49d },
                0.08d,
                0.08d,
                tuning));
    }

    [Fact]
    public void TrustedFixedScaleHistoryAcceptsStrongCorridorEvidence()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        const double scale = 0.56d;
        var candidate = new MapStructureCandidate
        {
            Scale = scale,
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 1.80d,
            EdgeCoverage = 0.76d,
            OccupancyCoverage = 0.84d,
            ReferenceCoverage = 0.487d,
            ProjectionCorrelation = 0.62d,
            ConsistentPartitions = 3
        };
        var request = CreateTrustedCorridorRequest(tuning, scale);

        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning,
                request: request));
    }

    [Fact]
    public void CorridorCoverageExceptionRequiresReliableFixedScaleHistory()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        const double scale = 0.56d;
        var candidate = new MapStructureCandidate
        {
            Scale = scale,
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 1.80d,
            EdgeCoverage = 0.76d,
            OccupancyCoverage = 0.84d,
            ReferenceCoverage = 0.487d,
            ProjectionCorrelation = 0.62d,
            ConsistentPartitions = 3
        };
        var coldRequest = CreateTrustedCorridorRequest(
            tuning,
            scale,
            includeHistory: false);

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning,
                request: coldRequest));
        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate with { ProjectionCorrelation = 0.59d },
                margin: 1d,
                requiredMargin: 0d,
                tuning,
                request: CreateTrustedCorridorRequest(tuning, scale)));
    }

    [Fact]
    public void CorridorCoverageExceptionKeepsOversizedOverlayBlocked()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        const double scale = 1.60d;
        var candidate = new MapStructureCandidate
        {
            Scale = scale,
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 1.50d,
            EdgeCoverage = 0.80d,
            OccupancyCoverage = 0.85d,
            ReferenceCoverage = 0.49d,
            ProjectionCorrelation = 0.70d,
            ConsistentPartitions = 4
        };

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning,
                request: CreateTrustedCorridorRequest(tuning, scale)));
    }

    [Fact]
    public void EdgeDegradedSilhouetteAcceptsBidirectionalStructureAgreement()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var candidate = new MapStructureCandidate
        {
            Scale = 0.5586d,
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 4.10d,
            EdgeCoverage = 0.372d,
            OccupancyCoverage = 0.805d,
            ReferenceCoverage = 0.743d,
            ProjectionCorrelation = 0.612d,
            ConsistentPartitions = 1
        };

        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning));
    }

    [Theory]
    [InlineData(0.34d, 0.805d, 0.743d, 0.612d)]
    [InlineData(0.372d, 0.77d, 0.743d, 0.612d)]
    [InlineData(0.372d, 0.805d, 0.69d, 0.612d)]
    [InlineData(0.372d, 0.805d, 0.743d, 0.59d)]
    public void EdgeDegradedSilhouetteRejectsOneSidedOrWeakAgreement(
        double edgeCoverage,
        double occupancyCoverage,
        double referenceCoverage,
        double projectionCorrelation)
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var candidate = new MapStructureCandidate
        {
            Scale = 0.5586d,
            IsWithinValidBounds = true,
            PriorAgreement = 1d,
            ChamferPixels = 4.10d,
            EdgeCoverage = edgeCoverage,
            OccupancyCoverage = occupancyCoverage,
            ReferenceCoverage = referenceCoverage,
            ProjectionCorrelation = projectionCorrelation,
            ConsistentPartitions = 1
        };

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: 0d,
                tuning));
    }

    private static MapStructureRegistrationRequest CreateTrustedCorridorRequest(
        MapStructureRegistrationTuning tuning,
        double scale,
        bool includeHistory = true) =>
        new()
        {
            Channel = MapAlignmentChannel.LowStructure,
            ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
            ViewportBounds = new MapScreenRect(0d, 0d, 1329d, 1060d),
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                ReferenceWidth = 1706,
                ReferenceHeight = 1504,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = tuning,
            CandidateHistory = includeHistory
                ? Enumerable.Range(0, 3)
                    .Select(index => new MapSimilarityTransform
                    {
                        Scale = scale,
                        TranslationX = 900d - (index * 40d),
                        TranslationY = 500d
                    })
                    .ToArray()
                : []
        };
}
