using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;

public sealed class MapFloorCanonicalizationRepositoryTests
{
    [Fact]
    public async Task SingleFloorDraftSavesAndReloadsWithoutPhantomSecondFloor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "first.png");
            using (var image = new Mat(new Size(160, 100), MatType.CV_8UC3, Scalar.All(255)))
                Assert.True(Cv2.ImWrite(source, image));

            var recognition = CreateRecognitionWithGateMarkers();
            recognition.Floors = new Dictionary<string, FloorRecognitionProfile>
            {
                ["1f"] = recognition.FirstFloor
            };
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorPaths = new Dictionary<string, string> { ["1f"] = source },
                Floors =
                [
                    new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }
                ],
                Recognition = recognition
            });

            Assert.Single(saved.Floors);
            Assert.Single(saved.Recognition.Floors);
            Assert.Equal("1f", saved.Recognition.FirstFloor.FloorKey);
            Assert.NotSame(saved.Recognition.FirstFloor, saved.Recognition.SecondFloor);

            var draft = await repository.CreateDraftAsync(saved.Id);
            Assert.NotNull(draft);
            draft!.Recognition.GetFloor("1f")!.Annotations.Add(
                new MapAnnotation { Type = MapAnnotationType.Outline });
            await repository.SaveAsync(draft);

            var reloaded = (await repository.GetMapsAsync()).Single();
            Assert.Single(reloaded.Floors);
            Assert.Single(reloaded.Recognition.Floors);
            Assert.Single(reloaded.Recognition.GetFloor("1f")!.Annotations);
            Assert.Empty(reloaded.Recognition.SecondFloor.Annotations);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogMigrationRepairsWrongSingleFloorEnumAndCreatesBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        var mapId = Guid.NewGuid();
        var mapDirectory = Path.Combine(root, mapId.ToString("N"));
        Directory.CreateDirectory(mapDirectory);
        try
        {
            using (var image = new Mat(new Size(64, 48), MatType.CV_8UC3, Scalar.All(255)))
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, "floor-1.png"), image));

            var recognition = CreateRecognitionWithGateMarkers();
            recognition.Floors = new Dictionary<string, FloorRecognitionProfile>
            {
                ["1f"] = recognition.FirstFloor
            };
            var map = new MapRecord
            {
                Id = mapId,
                SequenceNumber = 1,
                FloorOneFileName = "floor-1.png",
                FloorTwoFileName = string.Empty,
                Floors =
                [
                    new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }
                ],
                Recognition = recognition,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var catalog = JsonSerializer.SerializeToNode(new
            {
                StorageSchemaVersion = 13,
                NextSequenceNumber = 2,
                Maps = new[] { map }
            })!.AsObject();
            var floorProfile = catalog["Maps"]![0]!["Recognition"]!["Floors"]!["1f"]!.AsObject();
            floorProfile["Floor"] = 2;
            var catalogPath = Path.Combine(root, "maps.json");
            await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            var repository = new MapRepository(root);
            var maps = await repository.GetMapsAsync();
            var migrated = Assert.Single(maps);

            Assert.Equal(MapFloor.First, migrated.Recognition.GetFloor("1f")!.Floor);
            Assert.Single(migrated.Recognition.Floors);
            Assert.True(File.Exists($"{catalogPath}.bak-v14"));
            Assert.Contains("\"Floor\": 2", await File.ReadAllTextAsync($"{catalogPath}.bak-v14"));

            using var current = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath));
            Assert.Equal(17, current.RootElement.GetProperty("StorageSchemaVersion").GetInt32());
            Assert.Equal(
                1,
                current.RootElement
                    .GetProperty("Maps")[0]
                    .GetProperty("Recognition")
                    .GetProperty("Floors")
                    .GetProperty("1f")
                    .GetProperty("Floor")
                    .GetInt32());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MapRecognitionProfile CreateRecognitionWithGateMarkers()
    {
        var recognition = new MapRecognitionProfile();
        recognition.EnsureStandardAnchors();
        recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.1d, Height = 0.1d };
        recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.8d, Y = 0.8d, Width = 0.1d, Height = 0.1d };
        return recognition;
    }
}
