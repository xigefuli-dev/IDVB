using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapFeatureCacheTests
{
    [Fact]
    public void CrossResolutionCacheRequiresContentScaleEstimation()
    {
        var mapId = Guid.NewGuid();
        var source = new MapCacheResolutionSignature(
            2560, 1600, 1354, 1087);
        var target = new MapCacheResolutionSignature(
            1920, 1080, 990, 787);
        var entry = CreateTrustedEntry(
            mapId, "content", "1f", source, 1.064945491226174d,
            MapFeatureCacheSource.Player, successfulValidations: 0);

        Assert.False(MapScaleSeedResolver.TryResolve(
            [entry], mapId, "content", "1f", target, 0.70d, 0.04d,
            out var resolved, out var rejection));
        Assert.Null(resolved);
        Assert.Equal(
            "cross-resolution-cache-requires-content-scale",
            rejection);
    }

    [Theory]
    [InlineData(990, 787)]
    [InlineData(990, 700)]
    public void ViewportGeometryNeverDerivesScale(
        int viewportWidth,
        int viewportHeight)
    {
        var mapId = Guid.NewGuid();
        var source = new MapCacheResolutionSignature(
            2560, 1600, 1354, 1087);
        var target = new MapCacheResolutionSignature(
            1920, 1080, viewportWidth, viewportHeight);
        var entry = CreateTrustedEntry(
            mapId, "content", "b1f", source, 0.466d,
            MapFeatureCacheSource.Player, successfulValidations: 0);

        Assert.False(MapScaleSeedResolver.TryResolve(
            [entry], mapId, "content", "b1f", target,
            0.70d, 0.04d, out var resolved, out var reason));
        Assert.Null(resolved);
        Assert.Equal(
            "cross-resolution-cache-requires-content-scale",
            reason);
    }

    [Fact]
    public void DirectTrustedSourceOutranksValidatedAutomaticSources()
    {
        var mapId = Guid.NewGuid();
        var target = new MapCacheResolutionSignature(
            1920, 1080, 990, 787);
        var player = CreateTrustedEntry(
            mapId, "content", "1f", target, 1.064d,
            MapFeatureCacheSource.Player, 0,
            localizationConfidence: 0.80d,
            candidateMargin: 0.05d);
        var recovery = CreateTrustedEntry(
            mapId, "content", "1f", target, 1.12d,
            MapFeatureCacheSource.Recovery, 8,
            localizationConfidence: 0.99d,
            candidateMargin: 0.30d);

        Assert.True(MapScaleSeedResolver.TryResolve(
            [recovery, player], mapId, "content", "1f", target,
            0.70d, 0.04d, out var resolved, out _));
        Assert.Same(player, resolved!.CacheEntry);
    }

    [Fact]
    public void VpsgFixedScaleValidationCannotExceedThreePixelChamfer()
    {
        var source = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 8d,
            RestrictedSearchMaximumChamferPixels = 3d,
            ScaleSearchRadius = 0.04d
        };

        var strict =
            MapScaleSeedResolver.CreateStrictVpsgValidationTuning(source);

        Assert.Equal(3d, strict.MaximumChamferPixels);
        Assert.Equal(3d, strict.RestrictedSearchMaximumChamferPixels);
        Assert.Equal(0.04d, strict.ScaleSearchRadius);
        Assert.Equal(3d, source.MaximumChamferPixels);
    }

    [Fact]
    public void ExactTrustedCacheWinsWhenOtherResolutionAlsoExists()
    {
        var mapId = Guid.NewGuid();
        var target = new MapCacheResolutionSignature(
            1920, 1080, 990, 787);
        var source = new MapCacheResolutionSignature(
            2560, 1600, 1354, 1087);
        var entries = new[]
        {
            CreateTrustedEntry(
                mapId, "content", "1f", source, 1.064945491226174d,
                MapFeatureCacheSource.Player, successfulValidations: 0),
            CreateTrustedEntry(
                mapId, "content", "1f", target, 0.78d,
                MapFeatureCacheSource.Recovery, successfulValidations: 2)
        };

        Assert.True(MapScaleSeedResolver.TryResolve(
            entries, mapId, "content", "1f", target,
            0.70d, 0.04d, out var resolved, out _));
        Assert.NotNull(resolved);
        Assert.Equal(MapScaleSeedSource.ExactCache, resolved!.Source);
        Assert.Equal(0.78d, resolved.Scale);
    }

    [Fact]
    public void ResolverRejectsDistrustedAndIsolatesContentAndFloor()
    {
        var mapId = Guid.NewGuid();
        var source = new MapCacheResolutionSignature(
            2560, 1600, 1354, 1087);
        var target = new MapCacheResolutionSignature(
            1920, 1080, 990, 700);
        var failed = CreateTrustedEntry(
            mapId, "content", "1f", source, 1.064d,
            MapFeatureCacheSource.Player, successfulValidations: 0,
            failedValidations: 2);
        var wrongContent = CreateTrustedEntry(
            mapId, "other-content", "1f", source, 1.064d,
            MapFeatureCacheSource.Player, successfulValidations: 0);
        var wrongFloor = CreateTrustedEntry(
            mapId, "content", "2f", source, 1.064d,
            MapFeatureCacheSource.Player, successfulValidations: 0);
        var valid = CreateTrustedEntry(
            mapId, "content", "1f", source, 1.064d,
            MapFeatureCacheSource.Player, successfulValidations: 0);

        Assert.False(MapScaleSeedResolver.TryResolve(
            [failed, wrongContent, wrongFloor], mapId, "content", "1f",
            target, 0.70d, 0.04d, out _, out _));
        Assert.False(MapScaleSeedResolver.TryResolve(
            [valid], mapId, "content", "1f", target,
            0.70d, 0.04d, out _, out var rejection));
        Assert.Equal(
            "cross-resolution-cache-requires-content-scale",
            rejection);
    }

    [Fact]
    public void AutomaticEstimateRequiresApprovedSourceAndValidation()
    {
        var mapId = Guid.NewGuid();
        var source = new MapCacheResolutionSignature(
            2560, 1600, 1354, 1087);
        var target = new MapCacheResolutionSignature(
            1920, 1080, 990, 787);
        var automatic = CreateTrustedEntry(
            mapId, "content", "1f", source, 1.064d,
            MapFeatureCacheSource.Automatic, 5);
        var weakRecovery = CreateTrustedEntry(
            mapId, "content", "1f", source, 1.064d,
            MapFeatureCacheSource.Recovery, 5,
            localizationConfidence: 0.69d);

        Assert.False(MapScaleSeedResolver.TryResolve(
            [automatic, weakRecovery], mapId, "content", "1f", target,
            0.70d, 0.04d, out _, out _));
    }
}
