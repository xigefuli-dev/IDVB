using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class LowStructureAlignmentPlanTests
{
    [Fact]
    public void AutomaticLowCacheCannotBecomeTrustedByFixedScaleSelfConfirmation()
    {
        var entry = new MapFeatureCacheEntry
        {
            Key = new MapFeatureCacheKey(
                Guid.NewGuid(),
                "content",
                "b1f",
                new MapCacheResolutionSignature(2560, 1600, 1329, 1060),
                Channel: "low_structure"),
            Scale = new MapScaleCachePayload
            {
                UniformScale = 0.5585638453798261,
                Source = MapFeatureCacheSource.Automatic,
                SampleCount = 60,
                Confidence = 0.884,
                UpdatedAt = DateTimeOffset.UtcNow,
                Validation = new MapScaleCacheValidationMetadata
                {
                    LowStructureTrustLevel = LowStructureCacheTrustLevel.Trusted,
                    SuccessfulValidationCount = 60,
                    LastValidatedAt = DateTimeOffset.UtcNow
                }
            }
        };

        Assert.False(MapFeatureCacheRules.IsCacheEntryTrusted(entry));
    }

    [Fact]
    public void OnlyScaleSearchRoutesCanConfirmLowStructureScale()
    {
        Assert.False(LowStructureScaleEvidenceRules.IsIndependentScaleRoute(
            LowStructureAlignmentRoute.CachedFixed.ToString()));
        Assert.True(LowStructureScaleEvidenceRules.IsIndependentScaleRoute(
            LowStructureAlignmentRoute.SparseCoarseSeed.ToString()));
        Assert.True(LowStructureScaleEvidenceRules.IsIndependentScaleRoute(
            LowStructureAlignmentRoute.IncrementalRecovery.ToString()));
        Assert.Equal(
            5,
            LowStructureScaleEvidenceRules.MinimumIndependentScaleConfirmations);
        Assert.Equal(
            0.006d,
            LowStructureScaleEvidenceRules.MaximumLockRelativeDifference);
    }

    [Theory]
    [InlineData(0.641543769d, 0.644725177d)]
    [InlineData(0.573969323d, 0.576815630d)]
    public void QuantizedAdjacentScalesShareOneLowStructureBasin(
        double first,
        double second)
    {
        var resolution = LowStructureScaleEvidenceRules.RelativeDifference(
            first,
            second);
        var tolerance = LowStructureScaleEvidenceRules.ResolveClusterTolerance(
            resolution);

        Assert.InRange(resolution, 0.0049d, 0.0050d);
        Assert.InRange(tolerance, 0.0059d, 0.006d);
        Assert.True(
            LowStructureScaleEvidenceRules.RelativeDifference(first, second)
                <= tolerance);
        Assert.False(
            LowStructureScaleEvidenceRules.RelativeDifference(0.57d, 1.36d)
                <= tolerance);
    }
}
