using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IDVBuff.Tests;
public sealed partial class MapRepositoryTests
{

    [Fact]
    public async Task SaveStoresImagesLocallyWithoutBase64AndSurvivesSourceDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "sources");
        var mapDirectory = Path.Combine(root, "maps");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            var firstSource = Path.Combine(sourceDirectory, "selected-first.png");
            var secondSource = Path.Combine(sourceDirectory, "selected-second.png");
            using (var image = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(255)))
            {
                Assert.True(Cv2.ImWrite(firstSource, image));
                Assert.True(Cv2.ImWrite(secondSource, image));
            }

            var repository = new MapRepository(mapDirectory);
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = firstSource,
                FloorTwoPath = secondSource,
                Recognition = CreateRecognitionWithGateMarkers()
            });

            var catalogPath = Path.Combine(mapDirectory, "maps.json");
            var catalogJson = await File.ReadAllTextAsync(catalogPath);
            Assert.DoesNotContain("FloorImageBase64", catalogJson, StringComparison.Ordinal);
            Assert.DoesNotContain(firstSource, catalogJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secondSource, catalogJson, StringComparison.OrdinalIgnoreCase);

            var localFirstPath = repository.GetFloorOnePath(saved);
            var localSecondPath = repository.GetFloorTwoPath(saved);
            Assert.True(File.Exists(localFirstPath));
            Assert.True(File.Exists(localSecondPath));
            Assert.StartsWith(Path.GetFullPath(mapDirectory), Path.GetFullPath(localFirstPath), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(mapDirectory), Path.GetFullPath(localSecondPath), StringComparison.OrdinalIgnoreCase);

            File.Delete(firstSource);
            File.Delete(secondSource);

            var reloaded = (await new MapRepository(mapDirectory).GetMapsAsync()).Single();
            Assert.True(File.Exists(repository.GetFloorImagePath(reloaded, "1f")));
            Assert.True(File.Exists(repository.GetFloorImagePath(reloaded, "2f")));
            using var reloadedImage = Cv2.ImRead(repository.GetFloorImagePath(reloaded, "1f"), ImreadModes.Unchanged);
            Assert.False(reloadedImage.Empty());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyCatalogRemovesBase64WhenLocalImagesExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var map = CreateCatalogMapRecord();
            var mapDirectory = Path.Combine(root, map.Id.ToString("N"));
            Directory.CreateDirectory(mapDirectory);
            using (var image = new Mat(new Size(32, 24), MatType.CV_8UC3, Scalar.All(255)))
            {
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorOneFileName), image));
                Assert.True(Cv2.ImWrite(Path.Combine(mapDirectory, map.FloorTwoFileName), image));
            }

            var mapJson = JsonSerializer.SerializeToNode(map)!.AsObject();
            mapJson["FloorImageBase64"] = new JsonObject
            {
                ["1f"] = "legacy-inline-image",
                ["2f"] = "legacy-inline-image"
            };
            var catalogJson = new JsonObject
            {
                ["NextSequenceNumber"] = 2,
                ["Maps"] = new JsonArray(mapJson)
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var catalogPath = Path.Combine(root, "maps.json");
            await File.WriteAllTextAsync(catalogPath, catalogJson);

            var repository = new MapRepository(root);
            Assert.Single(await repository.GetMapsAsync());

            var cleaned = await File.ReadAllTextAsync(catalogPath);
            Assert.DoesNotContain("FloorImageBase64", cleaned, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy-inline-image", cleaned, StringComparison.Ordinal);
            Assert.Contains("StorageSchemaVersion", cleaned, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepairImageMetadataBackfillsStampsAndThumbnails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(255)))
                Assert.True(Cv2.ImWrite(source, image));

            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = source,
                FloorTwoPath = source,
                Recognition = CreateRecognitionWithGateMarkers()
            });
            var catalogPath = Path.Combine(root, "maps", "maps.json");
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
            var firstFloor = catalog["Maps"]![0]!["Floors"]![0]!.AsObject();
            firstFloor["ImageFileLength"] = 0;
            firstFloor["ImageLastWriteUtcTicks"] = 0;
            firstFloor["ThumbnailFileName"] = "";
            firstFloor["ThumbnailFileLength"] = 0;
            firstFloor["ThumbnailLastWriteUtcTicks"] = 0;
            catalog["StorageSchemaVersion"] = 10;
            await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Assert.Single(await repository.GetMapsAsync());
            await repository.RepairImageMetadataAsync(CancellationToken.None);

            var repaired = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
            var repairedFloor = repaired["Maps"]![0]!["Floors"]![0]!.AsObject();
            Assert.True(repairedFloor["ImageFileLength"]!.GetValue<long>() > 0);
            Assert.True(repairedFloor["ImageLastWriteUtcTicks"]!.GetValue<long>() > 0);
            Assert.False(string.IsNullOrWhiteSpace(repairedFloor["ThumbnailFileName"]!.GetValue<string>()));
            Assert.Equal(17, repaired["StorageSchemaVersion"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedLocalImageIsRejectedByExplicitBindingMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "selected.png");
            using (var image = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(255)))
                Assert.True(Cv2.ImWrite(source, image));

            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = source,
                FloorTwoPath = source,
                Recognition = CreateRecognitionWithGateMarkers()
            });

            using (var changedImage = new Mat(new Size(96, 64), MatType.CV_8UC3, Scalar.All(0)))
                Assert.True(Cv2.ImWrite(repository.GetFloorOnePath(saved), changedImage));

            Assert.Single(await repository.GetMapsAsync());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new MapRepository(Path.Combine(root, "maps")).VerifyMapContentAsync(saved.Id));
            Assert.Contains("metadata does not match", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyCatalogMigrationFailsWithoutLocalImagesAndLeavesCatalogUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var map = CreateCatalogMapRecord();
            var mapJson = JsonSerializer.SerializeToNode(map)!.AsObject();
            mapJson["FloorImageBase64"] = new JsonObject
            {
                ["1f"] = "legacy-inline-image",
                ["2f"] = "legacy-inline-image"
            };
            var original = new JsonObject
            {
                ["NextSequenceNumber"] = 2,
                ["Maps"] = new JsonArray(mapJson)
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var catalogPath = Path.Combine(root, "maps.json");
            await File.WriteAllTextAsync(catalogPath, original);

            var repository = new MapRepository(root);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.GetMapsAsync());

            Assert.Contains(map.Id.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("missing its local image", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(catalogPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MapRecord CreateCatalogMapRecord()
    {
        return new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = 1,
            FloorOneFileName = "floor-1.png",
            FloorTwoFileName = "floor-2.png",
            Recognition = CreateRecognitionWithGateMarkers(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
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
