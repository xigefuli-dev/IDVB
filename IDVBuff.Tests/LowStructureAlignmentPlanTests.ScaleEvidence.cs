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
            0.003d,
            LowStructureScaleEvidenceRules.MaximumLockRelativeDifference);
    }
}
