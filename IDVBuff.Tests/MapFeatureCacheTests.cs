using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapFeatureCacheTests
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
            Assert.Equal(2, migrated!.SchemaVersion);
            Assert.Equal(2, migrated.Scale.SchemaVersion);
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
    public void LegacyDisplayCalibrationMigratesAndExactPhysicalProfileWins()
    {
        var settings = new MapRuntimeSettings
        {
            SchemaVersion = 9,
            MapViewportRegion = new NormalizedRectangle
            {
                X = 0.1,
                Y = 0.2,
                Width = 0.7,
                Height = 0.6
            },
            CalibrationClientWidth = 1920,
            CalibrationClientHeight = 1080,
            CalibrationVersion = MapRuntimeSettings.CurrentCalibrationVersion
        };

        settings.Normalize();
        var migrated = settings.GetExactDisplayCalibration(1920, 1080);
        Assert.NotNull(migrated);
        Assert.Equal(MapDisplayCalibrationSource.Migrated, migrated!.Source);

        settings.UpsertMapViewportCalibration(
            new NormalizedRectangle
            {
                X = 0.15,
                Y = 0.1,
                Width = 0.55,
                Height = 0.7
            },
            2560,
            1600,
            observedDpi: 144);
        var exact = settings.ResolveMapViewportRegion(2560, 1600);
        Assert.NotNull(exact);
        Assert.Equal(0.55, exact!.Width, 6);
        Assert.Equal(
            MapDisplayCalibrationSource.Exact,
            settings.GetExactDisplayCalibration(2560, 1600)!.Source);
    }

    private static MapRecord CreateMap() => new()
    {
        Id = Guid.NewGuid(),
        ContentVersion = 3,
        UpdatedAt = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
        Floors =
        [
            new FloorDefinition
            {
                Key = "1f",
                SortOrder = 1,
                ImageSha256 = "image",
                ImageWidth = 1000,
                ImageHeight = 800,
                RecognitionSha256 = "recognition",
                RecognitionWidth = 1000,
                RecognitionHeight = 800,
                OverlaySha256 = "overlay",
                OverlayWidth = 1000,
                OverlayHeight = 800
            },
            new FloorDefinition { Key = "2f", SortOrder = 2 }
        ]
    };
}
