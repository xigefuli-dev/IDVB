using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed partial class MapFeatureCacheTests
{
    [Fact]
    public void LegacySettingsUpgradeWithCacheFeaturesDisabled()
    {
        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(
            "{\"SchemaVersion\":8}")!;

        settings.Normalize();

        Assert.Equal(MapRuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.False(settings.AllowAutomaticMapCache);
        Assert.False(settings.SaveMapCacheBinding.IsConfigured);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(2560, 1600)]
    public void OnlyExactSupportedClientResolutionsAreAccepted(
        int width,
        int height)
    {
        Assert.True(new MapCacheResolutionSignature(
            width, height, width - 100, height - 100).IsSupported);
        Assert.False(new MapCacheResolutionSignature(
            width - 1, height, width - 100, height - 100).IsSupported);
    }

    [Fact]
    public void CacheKeysIsolateMapFloorAndPhysicalViewportButNotDpi()
    {
        var mapId = Guid.NewGuid();
        var resolution = new MapCacheResolutionSignature(
            1920, 1080, 1600, 900);
        var baseline = new MapFeatureCacheKey(mapId, "content", "1f", resolution);

        Assert.NotEqual(baseline, baseline with { MapId = Guid.NewGuid() });
        Assert.NotEqual(baseline, baseline with { FloorKey = "2f" });
        Assert.NotEqual(baseline, baseline with
        {
            Resolution = resolution with { ViewportWidth = 1599 }
        });
        var dpi120 = MapCacheResolutionSignature.FromBounds(
            new MapScreenRect(0, 0, 1920, 1080),
            new MapScreenRect(0, 0, 1600, 900),
            120);
        var dpi144 = MapCacheResolutionSignature.FromBounds(
            new MapScreenRect(0, 0, 1920, 1080),
            new MapScreenRect(0, 0, 1600, 900),
            144);
        Assert.Equal(dpi120, dpi144);
    }

    [Fact]
    public void ReplacingSeedScaleKeepsObservedGateAtSameScreenCenter()
    {
        var seed = new MapAlignmentSession
        {
            MapId = Guid.NewGuid(),
            FloorKey = "1f",
            BaselineGateScale = 0.72d,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = 0.72d,
                ScaleY = 0.72d,
                ReferenceCenterX = 500d,
                ReferenceCenterY = 400d,
                ScreenCenterX = 760d,
                ScreenCenterY = 510d,
                OffsetX = 400d,
                OffsetY = 222d,
                ReferenceWidth = 1000,
                ReferenceHeight = 800
            }
        };

        var projected = seed.WithUniformScale(0.774833230978016d);

        Assert.Equal(seed.LockedTransform.ScreenCenterX,
            projected.LockedTransform.ScreenCenterX);
        Assert.Equal(seed.LockedTransform.ScreenCenterY,
            projected.LockedTransform.ScreenCenterY);
        Assert.Equal(760d - (500d * 0.774833230978016d),
            projected.LockedTransform.OffsetX, 12);
        Assert.Equal(510d - (400d * 0.774833230978016d),
            projected.LockedTransform.OffsetY, 12);
    }

    [Fact]
    public void FingerprintChangesWhenMapContentChanges()
    {
        var map = CreateMap();
        var original = MapFeatureCacheRules.ComputeContentFingerprint(map);
        map.Floors[0].RecognitionSha256 = "replacement";

        Assert.NotEqual(
            original,
            MapFeatureCacheRules.ComputeContentFingerprint(map));
    }

    [Fact]
    public void NearbySamplesFormStableClusterAndExcludeOutlier()
    {
        var samples = new[]
        {
            new MapScaleSample(1.000, 0.90),
            new MapScaleSample(1.004, 0.80),
            new MapScaleSample(0.997, 0.85),
            new MapScaleSample(1.120, 0.99)
        };

        Assert.True(MapScaleSampleAggregator.TryAggregate(samples, out var result));
        Assert.NotNull(result);
        Assert.Equal(3, result!.SampleCount);
        Assert.InRange(result.Scale, 0.997, 1.004);
    }

    [Fact]
    public void TooFewOrUnstableSamplesDoNotUpdate()
    {
        Assert.False(MapScaleSampleAggregator.TryAggregate(
            [new(1.0, 0.9), new(1.001, 0.9)], out _));
        Assert.False(MapScaleSampleAggregator.TryAggregate(
            [new(0.8, 0.9), new(1.0, 0.9), new(1.2, 0.9)], out _));
    }

    [Fact]
    public async Task RepositoryDoesNotReuseEntryAfterFingerprintChange()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"idvb-map-cache-{Guid.NewGuid():N}");
        try
        {
            var repository = new MapFeatureCacheRepository(directory);
            await repository.InitializeAsync();
            var map = CreateMap();
            var resolution = new MapCacheResolutionSignature(
                1920, 1080, 1600, 900);
            var originalKey = MapFeatureCacheRules.CreateKey(map, "1f", resolution);
            await repository.UpsertAsync(new MapFeatureCacheEntry
            {
                Key = originalKey,
                Scale = new MapScaleCachePayload
                {
                    UniformScale = 1.02,
                    Source = MapFeatureCacheSource.Manual,
                    SampleCount = 1,
                    Confidence = 0.9,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            });

            map.ContentVersion++;
            var changedKey = MapFeatureCacheRules.CreateKey(map, "1f", resolution);
            Assert.False(repository.TryGet(changedKey, out _));
            Assert.True(repository.TryGet(originalKey, out _));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyDpiSplitEntriesMigrateToOnePhysicalGeometryKey()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"idvb-map-cache-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var mapId = Guid.NewGuid();
            var updated = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
            var json = $$"""
                {
                  "SchemaVersion": 1,
                  "Entries": [
                    {
                      "SchemaVersion": 1,
                      "Key": {
                        "MapId": "{{mapId}}",
                        "MapContentFingerprint": "fingerprint",
                        "FloorKey": "2f",
                        "Resolution": {
                          "ClientWidth": 2560,
                          "ClientHeight": 1600,
                          "ViewportWidth": 1421,
                          "ViewportHeight": 1249,
                          "Dpi": 120
                        }
                      },
                      "Scale": {
                        "SchemaVersion": 1,
                        "UniformScale": 1.337,
                        "Source": 1,
                        "SampleCount": 3,
                        "Confidence": 0.8,
                        "RelativeMedianAbsoluteDeviation": 0.002,
                        "UpdatedAt": "{{updated:O}}"
                      }
                    },
                    {
                      "SchemaVersion": 1,
                      "Key": {
                        "MapId": "{{mapId}}",
                        "MapContentFingerprint": "fingerprint",
                        "FloorKey": "2f",
                        "Resolution": {
                          "ClientWidth": 2560,
                          "ClientHeight": 1600,
                          "ViewportWidth": 1421,
                          "ViewportHeight": 1249,
                          "Dpi": 144
                        }
                      },
                      "Scale": {
                        "SchemaVersion": 1,
                        "UniformScale": 1.339,
                        "Source": 1,
                        "SampleCount": 4,
                        "Confidence": 0.9,
                        "RelativeMedianAbsoluteDeviation": 0.001,
                        "UpdatedAt": "{{updated.AddMinutes(1):O}}"
                      }
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(directory, "map-feature-cache.json"),
                json);
            var repository = new MapFeatureCacheRepository(directory);
            await repository.InitializeAsync();
            var key = new MapFeatureCacheKey(
                mapId,
                "fingerprint",
                "2f",
                new MapCacheResolutionSignature(2560, 1600, 1421, 1249));

            Assert.True(repository.TryGet(key, out var migrated));
            Assert.NotNull(migrated);
            Assert.Equal(MapFeatureCacheSchema.CurrentVersion, migrated!.SchemaVersion);
            Assert.Equal(
                MapFeatureCacheSchema.CurrentVersion,
                migrated.Scale.SchemaVersion);
            Assert.Equal(7, migrated.Scale.SampleCount);
            Assert.InRange(migrated.Scale.UniformScale, 1.337, 1.339);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyManualCacheRemainsDirectlyTrustedAfterMigration()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"idvb-manual-cache-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var mapId = Guid.NewGuid();
            var updated = DateTimeOffset.UtcNow;
            var json = $$"""
                {
                  "SchemaVersion": 2,
                  "Entries": [{
                    "SchemaVersion": 2,
                    "Key": {
                      "MapId": "{{mapId}}",
                      "MapContentFingerprint": "trusted-content",
                      "FloorKey": "2f",
                      "Resolution": {
                        "ClientWidth": 2560,
                        "ClientHeight": 1600,
                        "ViewportWidth": 1421,
                        "ViewportHeight": 1249
                      }
                    },
                    "Scale": {
                      "SchemaVersion": 2,
                      "UniformScale": 1.337,
                      "Source": 0,
                      "SampleCount": 1,
                      "Confidence": 0.84,
                      "RelativeMedianAbsoluteDeviation": 0.0,
                      "UpdatedAt": "{{updated:O}}"
                    }
                  }]
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(directory, "map-feature-cache.json"),
                json);
            var repository = new MapFeatureCacheRepository(directory);

            await repository.InitializeAsync();

            var key = new MapFeatureCacheKey(
                mapId,
                "trusted-content",
                "2f",
                new MapCacheResolutionSignature(2560, 1600, 1421, 1249));
            Assert.True(repository.TryGet(key, out var migrated));
            Assert.NotNull(migrated);
            Assert.Equal(MapFeatureCacheSource.Manual, migrated!.Scale.Source);
            Assert.True(migrated.Scale.Validation!.DirectlyTrusted);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepairRequiresThreeSpatiallyConsistentHighQualitySamples()
    {
        var consistent = new[]
        {
            new MapCacheRepairSample(1.3370, 210.0, 330.0, 0.90, 0.06),
            new MapCacheRepairSample(1.3375, 211.0, 329.5, 0.88, 0.07),
            new MapCacheRepairSample(1.3368, 209.5, 331.0, 0.92, 0.05)
        };

        Assert.False(MapCacheRepairSampleAggregator.TryAggregate(
            consistent.Take(2).ToArray(),
            out _));
        Assert.True(MapCacheRepairSampleAggregator.TryAggregate(
            consistent,
            out var aggregate));
        Assert.Equal(3, aggregate!.SampleCount);

        var drifting = consistent.ToArray();
        drifting[2] = drifting[2] with { OffsetX = 220d };
        Assert.False(MapCacheRepairSampleAggregator.TryAggregate(
            drifting,
            out _));
    }

    [Fact]
    public void IdentityPriorCannotPassLocalizationCacheGate()
    {
        var result = new MapRecognitionResult
        {
            Confidence = 0.52d,
            IdentityConfidence = 0.90d,
            LocalizationConfidence = 0.52d,
            EvidenceKind = MapAlignmentEvidenceKind.Structure,
            StructureCandidateMargin = 0.08d
        };

        Assert.False(MapFeatureCacheRules.IsReliableLocalizationSample(
            result,
            minimumLocalizationConfidence: 0.70d,
            minimumCandidateMargin: 0.04d));
    }

    [Fact]
    public void ManualEntryCanOnlyBeReplacedByValidatedThreeSampleRecovery()
    {
        var key = new MapFeatureCacheKey(
            Guid.NewGuid(),
            "content",
            "2f",
            new MapCacheResolutionSignature(2560, 1600, 1421, 1249));
        MapFeatureCacheEntry Entry(
            MapFeatureCacheSource source,
            int samples,
            int validations) => new()
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 1.337d,
                Source = source,
                SampleCount = samples,
                Confidence = 0.9d,
                RelativeMedianAbsoluteDeviation = 0.001d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    SuccessfulValidationCount = validations,
                    LastLocalizationConfidence = 0.9d,
                    LastCandidateMargin = 0.06d,
                    LastValidatedAt = validations > 0
                        ? DateTimeOffset.UtcNow
                        : default
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };
        var manual = Entry(MapFeatureCacheSource.Manual, 1, 0);

        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            manual,
            Entry(MapFeatureCacheSource.Automatic, 4, 4)));
        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            manual,
            Entry(MapFeatureCacheSource.Recovery, 2, 2)));
        Assert.True(MapFeatureCacheRules.CanReplaceExistingEntry(
            manual,
            Entry(MapFeatureCacheSource.Recovery, 3, 3)));
    }

    [Fact]
    public void PlayerEntryIsProtectedFromAutomaticOverwrites()
    {
        var key = new MapFeatureCacheKey(
            Guid.NewGuid(),
            "content",
            "1f",
            new MapCacheResolutionSignature(1920, 1080, 1280, 720));
        MapFeatureCacheEntry Entry(
            MapFeatureCacheSource source,
            int samples,
            int validations) => new()
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = 1.0d,
                Source = source,
                SampleCount = samples,
                Confidence = 1.0d,
                RelativeMedianAbsoluteDeviation = 0d,
                Validation = new MapScaleCacheValidationMetadata
                {
                    SuccessfulValidationCount = validations,
                    LastLocalizationConfidence = 1.0d,
                    LastCandidateMargin = 0.06d,
                    LastValidatedAt = validations > 0
                        ? DateTimeOffset.UtcNow
                        : default
                },
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };
        var player = Entry(MapFeatureCacheSource.Player, 1, 0);

        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            player,
            Entry(MapFeatureCacheSource.Automatic, 4, 4)));
        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            player,
            Entry(MapFeatureCacheSource.Recovery, 2, 2)));
        Assert.False(MapFeatureCacheRules.CanReplaceExistingEntry(
            player,
            Entry(MapFeatureCacheSource.PreprocessedEstimate, 1, 1)));
        // Recovery with three consistent samples may still displace player
        // data, mirroring the Manual-entry rule.
        Assert.True(MapFeatureCacheRules.CanReplaceExistingEntry(
            player,
            Entry(MapFeatureCacheSource.Recovery, 3, 3)));
    }
}
