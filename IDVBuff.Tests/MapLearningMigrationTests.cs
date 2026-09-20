using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapLearningMigrationTests
{
    [Fact]
    public void SpatialLabel_BackProjectsViewportCenterInsteadOfMapCenter()
    {
        var map = new MapRecord { Id = Guid.NewGuid(), Class = "S1" };
        map.NormalizeRecognition();
        var choice = new MapRecognitionChoice
        {
            Recognition = new RuntimeMapRecognition
            {
                Map = map,
                Result = new MapRecognitionResult
                {
                    MapId = map.Id,
                    Floor = "1f",
                    LocalizationConfidence = 0.95d,
                    EvidenceKind = MapAlignmentEvidenceKind.Structure,
                    OverlayTransform = new MapOverlayTransform
                    {
                        ScaleX = 2d,
                        ScaleY = 2d,
                        OffsetX = 100d,
                        OffsetY = 100d,
                        ReferenceWidth = 1000,
                        ReferenceHeight = 800,
                        ReferenceCenterX = 500d,
                        ReferenceCenterY = 400d,
                        ScreenCenterX = 1100d,
                        ScreenCenterY = 900d
                    }
                }
            }
        };

        var resolved = MapLearningRepository.TryResolveSpatialLabel(
            choice,
            new Size(1000, 800),
            new MapScreenRect(800d, 200d, 1000d, 800d),
            out var x,
            out var y);

        Assert.True(resolved);
        Assert.Equal(0.6d, x, 6);
        Assert.Equal(0.35d, y, 6);
    }

    [Fact]
    public async Task LegacySampleMigration_RetainsOriginalAndUsesFullGuideFloors()
    {
        var appRoot = Path.Combine(Path.GetTempPath(),
            "idvb-map-learning-migration", Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(appRoot, "MapLearning");
        var mapsRoot = Path.Combine(appRoot, "Maps");
        var firstMap = Guid.NewGuid();
        var secondMap = Guid.NewGuid();
        Directory.CreateDirectory(mapsRoot);
        CreateFloor(mapsRoot, firstMap, Scalar.White);
        CreateFloor(mapsRoot, secondMap, new Scalar(128, 128, 128));
        await File.WriteAllTextAsync(Path.Combine(mapsRoot, "maps.json"),
            JsonSerializer.Serialize(new
            {
                maps = new[]
                {
                    MapEntry(firstMap),
                    MapEntry(secondMap)
                }
            }, JsonOptions));

        var repository = new MapLearningRepository(repositoryRoot);
        repository.EnsureCreated();
        var sample = new MapLearningSampleManifest
        {
            SchemaVersion = 1,
            SampleId = "legacy-sample",
            MatchId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            SelectedMapId = secondMap,
            Candidates =
            [
                LegacyCandidate(firstMap, isPositive: false),
                LegacyCandidate(secondMap, isPositive: true)
            ]
        };
        var sampleDirectory = Path.Combine(repository.SamplesDirectory,
            sample.SampleId);
        Directory.CreateDirectory(sampleDirectory);
        var manifestPath = Path.Combine(sampleDirectory, "manifest.json");
        var originalJson = JsonSerializer.Serialize(sample, JsonOptions);
        await File.WriteAllTextAsync(manifestPath, originalJson);

        var migration = await repository.MigrateLegacySamplesAsync(
            CancellationToken.None);

        Assert.Equal(1, migration.MigratedMatchCount);
        Assert.Equal(0, migration.SkippedMatchCount);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(Path.Combine(
            sampleDirectory, "manifest.schema-v1.json")));
        var upgraded = Assert.Single(await repository.LoadSamplesAsync(
            CancellationToken.None));
        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal(1, upgraded.MigratedFromSchemaVersion);
        Assert.Equal(secondMap, upgraded.SelectedMapId);
        Assert.All(upgraded.Candidates, candidate =>
        {
            Assert.Equal("floor", candidate.ReferenceScope);
            Assert.Equal("1f", candidate.FloorKey);
            Assert.Equal(500, candidate.ReferenceWidth);
            Assert.Equal(500, candidate.ReferenceHeight);
            Assert.True(File.Exists(Path.Combine(repository.ReferencesDirectory,
                candidate.ReferenceFile)));
        });
    }

    private static object MapEntry(Guid mapId) => new
    {
        id = mapId,
        floors = new[]
        {
            new
            {
                key = "1f",
                imageFileName = "floor-1.png",
                overlayFileName = "floor-1.png"
            }
        }
    };

    private static MapLearningCandidateManifest LegacyCandidate(
        Guid mapId,
        bool isPositive) => new()
    {
        MapId = mapId,
        FloorKey = "1f",
        ReferenceHash = "legacy",
        ReferenceFile = "legacy.png",
        IsPositive = isPositive
    };

    private static void CreateFloor(string mapsRoot, Guid mapId, Scalar color)
    {
        var directory = Path.Combine(mapsRoot, mapId.ToString("N"));
        Directory.CreateDirectory(directory);
        using var floor = new Mat(180, 360, MatType.CV_8UC3, color);
        Cv2.Rectangle(floor, new Rect(20, 30, 80, 60), Scalar.Black, 3);
        Cv2.ImWrite(Path.Combine(directory, "floor-1.png"), floor);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
