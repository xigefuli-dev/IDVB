using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;
public sealed partial class MapFeatureCacheTests
{

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

    private static MapFeatureCacheEntry CreateTrustedEntry(
        Guid mapId,
        string contentFingerprint,
        string floorKey,
        MapCacheResolutionSignature resolution,
        double scale,
        MapFeatureCacheSource source,
        int successfulValidations,
        int failedValidations = 0,
        double localizationConfidence = 0.90d,
        double candidateMargin = 0.08d) => new()
    {
        Key = new MapFeatureCacheKey(
            mapId,
            contentFingerprint,
            floorKey,
            resolution),
        Scale = new MapScaleCachePayload
        {
            UniformScale = scale,
            Source = source,
            SampleCount = 1,
            Confidence = localizationConfidence,
            RelativeMedianAbsoluteDeviation = 0d,
            Validation = new MapScaleCacheValidationMetadata
            {
                DirectlyTrusted = source is MapFeatureCacheSource.Manual
                    or MapFeatureCacheSource.Player,
                SuccessfulValidationCount = successfulValidations,
                FailedValidationCount = failedValidations,
                LastLocalizationConfidence = localizationConfidence,
                LastCandidateMargin = candidateMargin,
                LastValidatedAt = successfulValidations + failedValidations > 0
                    ? DateTimeOffset.UtcNow
                    : default
            },
            UpdatedAt = DateTimeOffset.UtcNow
        }
    };
}
