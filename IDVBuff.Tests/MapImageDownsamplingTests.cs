using System.Security.Cryptography;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapImageDownsamplingTests
{
    [Fact]
    public async Task ClassDownsamplingAlwaysRegeneratesFromThePreservedWholeImage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var source = new Mat(new Size(800, 400), MatType.CV_8UC3))
            {
                Cv2.Randu(source, Scalar.All(0), Scalar.All(255));
                Assert.True(Cv2.ImWrite(sourcePath, source));
            }
            var sourceHash = await HashAsync(sourcePath);
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.RecognitionRegion = new NormalizedRectangle
            {
                X = 0.25,
                Y = 0,
                Width = 0.5,
                Height = 1
            };
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.8, Y = 0.8, Width = 0.1, Height = 0.1 };
            recognition.FirstFloor.BackgroundLayers =
            [
                new MapBackgroundLayer
                {
                    BrushSizePixels = 64,
                    Points = [new NormalizedPoint { X = .5, Y = .5 }]
                }
            ];
            var repository = new MapRepository(Path.Combine(root, "maps"));
            await repository.SaveAsync(new MapDraft
            {
                Title = "Downsample",
                Class = "S1",
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                FloorPaths = new Dictionary<string, string> { ["1f"] = sourcePath },
                Recognition = recognition
            });

            await repository.SetClassImageDownsamplingAsync("S1", 8);
            var factorEight = Assert.Single(await repository.GetMapsAsync());
            Assert.Equal((100, 50), ReadSize(repository.GetFloorImagePath(factorEight, "1f")));
            Assert.Equal((50, 50), ReadSize(repository.GetFloorRecognitionPath(factorEight, "1f")));
            Assert.Equal(8, factorEight.Recognition.FirstFloor.BackgroundLayers[0].BrushSizePixels);

            await repository.SetClassImageDownsamplingAsync("S1", 2);
            var factorTwo = Assert.Single(await repository.GetMapsAsync());
            Assert.Equal((400, 200), ReadSize(repository.GetFloorImagePath(factorTwo, "1f")));
            Assert.Equal((200, 200), ReadSize(repository.GetFloorRecognitionPath(factorTwo, "1f")));
            Assert.Equal(32, factorTwo.Recognition.FirstFloor.BackgroundLayers[0].BrushSizePixels);

            await repository.SetClassImageDownsamplingAsync("S1", 0);
            var restored = Assert.Single(await repository.GetMapsAsync());
            Assert.Equal((800, 400), ReadSize(repository.GetFloorImagePath(restored, "1f")));
            Assert.Equal(sourceHash, await HashAsync(repository.GetFloorImagePath(restored, "1f")));
            Assert.Equal(0, restored.ClassProperties.ImageDownsampleFactor);
            Assert.Equal(64, restored.Recognition.FirstFloor.BackgroundLayers[0].BrushSizePixels);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(64, 0, 8, 8)]
    [InlineData(8, 8, 0, 64)]
    [InlineData(8, 8, 2, 32)]
    public void ConcealBrushTracksImageSamplingScale(
        int brushSize,
        int oldFactor,
        int newFactor,
        int expected)
    {
        var oldDivisor = oldFactor <= 1 ? 1d : oldFactor;
        var newDivisor = newFactor <= 1 ? 1d : newFactor;

        Assert.Equal(expected, MapRepository.ClampBrushSizeForImageScale(
            brushSize,
            oldDivisor / newDivisor));
    }

    [Fact]
    public async Task CombinedClassImageAndBackgroundChangeRebuildsEachMapOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var source = new Mat(new Size(80, 40), MatType.CV_8UC3, Scalar.White))
                Assert.True(Cv2.ImWrite(sourcePath, source));
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = .1, Y = .1, Width = .1, Height = .1 };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = .8, Y = .8, Width = .1, Height = .1 };
            var before = await repository.SaveAsync(new MapDraft
            {
                Title = "Combined",
                Class = "S1",
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                FloorPaths = new Dictionary<string, string> { ["1f"] = sourcePath },
                Recognition = recognition
            });

            await repository.SetClassImageDownsamplingAsync("S1", 2, true, 12);

            var after = Assert.Single(await repository.GetMapsAsync());
            Assert.Equal(before.ContentVersion + 1, after.ContentVersion);
            Assert.Equal(2, after.ClassProperties.ImageDownsampleFactor);
            Assert.True(after.ClassProperties.RemoveBackground);
            Assert.Equal(12, after.ClassProperties.BackgroundRemovalIntensity);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdvmExportUsesOriginalWhenAvailableAndCurrentImageWhenBackupMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var sourceImage = new Mat(new Size(800, 400), MatType.CV_8UC3, Scalar.White))
                Assert.True(Cv2.ImWrite(sourcePath, sourceImage));
            var source = new MapRepository(Path.Combine(root, "source"));
            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = .1, Y = .1, Width = .1, Height = .1 };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = .8, Y = .8, Width = .1, Height = .1 };
            recognition.FirstFloor.BackgroundLayers =
            [new MapBackgroundLayer { BrushSizePixels = 64 }];
            await source.SaveAsync(new MapDraft
            {
                Title = "Portable original",
                Class = "S1",
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                FloorPaths = new Dictionary<string, string> { ["1f"] = sourcePath },
                Recognition = recognition
            });
            await source.SetClassImageDownsamplingAsync("S1", 2);

            var package = Path.Combine(root, "map.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass, "S1", package);
            var target = new MapRepository(Path.Combine(root, "target"));
            var service = new IdvmPackageService(target);
            var imported = await service.ImportAsync(await service.InspectAsync(package));
            var importedMap = Assert.Single(imported.ImportedMaps);
            Assert.Equal((800, 400), ReadSize(target.GetFloorImagePath(importedMap, "1f")));

            await target.SetClassImageDownsamplingAsync(imported.CreatedClasses.Single(), 2);
            var resampled = Assert.Single(await target.GetMapsAsync());
            Assert.Equal((400, 200), ReadSize(target.GetFloorImagePath(resampled, "1f")));

            Directory.Delete(Path.Combine(root, "source", ".downsample-originals"), recursive: true);
            var fallbackPackage = Path.Combine(root, "fallback.idvm");
            await new IdvmPackageService(source).ExportAsync(
                IdvmExportScope.CurrentClass, "S1", fallbackPackage);
            var fallback = new MapRepository(Path.Combine(root, "fallback"));
            var fallbackService = new IdvmPackageService(fallback);
            var fallbackImport = await fallbackService.ImportAsync(
                await fallbackService.InspectAsync(fallbackPackage));
            Assert.Equal((400, 200), ReadSize(fallback.GetFloorImagePath(
                Assert.Single(fallbackImport.ImportedMaps), "1f")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingOriginalsCannotBeResampledOrRestored()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var source = new Mat(new Size(800, 400), MatType.CV_8UC3, Scalar.White))
                Assert.True(Cv2.ImWrite(sourcePath, source));
            var repository = new MapRepository(Path.Combine(root, "maps"));
            await repository.SaveAsync(new MapDraft
            {
                Title = "No original",
                Class = "S1",
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                FloorPaths = new Dictionary<string, string> { ["1f"] = sourcePath },
                Recognition = CreateRequiredRecognition()
            });
            await repository.SetClassImageDownsamplingAsync("S1", 2);
            var sampled = Assert.Single(await repository.GetMapsAsync());
            Directory.Delete(Path.Combine(root, "maps", ".downsample-originals"), recursive: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SetClassImageDownsamplingAsync("S1", 0));
            var unchanged = Assert.Single(await repository.GetMapsAsync());
            Assert.Equal(2, unchanged.ClassProperties.ImageDownsampleFactor);
            Assert.Equal((400, 200), ReadSize(repository.GetFloorImagePath(unchanged, "1f")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAcceptsAnImageFromAUnicodePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var asciiPath = Path.Combine(root, "source.png");
            using (var source = new Mat(new Size(80, 40), MatType.CV_8UC3, Scalar.White))
                Assert.True(Cv2.ImWrite(asciiPath, source));
            var unicodePath = Path.Combine(root, "展十🗺️地图.png");
            File.Copy(asciiPath, unicodePath);
            var repository = new MapRepository(Path.Combine(root, "maps"));

            var saved = await repository.SaveAsync(new MapDraft
            {
                Title = "Unicode input",
                Class = "S1",
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                FloorPaths = new Dictionary<string, string> { ["1f"] = unicodePath },
                Recognition = CreateRequiredRecognition()
            });

            Assert.Equal((80, 40), ReadSize(repository.GetFloorImagePath(saved, "1f")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NewMapInADownsampledClassIsSavedFromItsOriginalAtTheClassScale()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var asciiPath = Path.Combine(root, "source.png");
            using (var source = new Mat(new Size(800, 400), MatType.CV_8UC3, Scalar.White))
                Assert.True(Cv2.ImWrite(asciiPath, source));
            var sourcePath = Path.Combine(root, "展十🗺️原图.png");
            File.Copy(asciiPath, sourcePath);
            var repository = new MapRepository(Path.Combine(root, "maps"));
            await repository.CreateClassAsync("S2");
            await repository.SetClassImageDownsamplingAsync("S2", 2);

            var saved = await repository.SaveAsync(new MapDraft
            {
                Title = "Inherited downsampling",
                Class = "S2",
                Floors = [new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }],
                FloorPaths = new Dictionary<string, string> { ["1f"] = sourcePath },
                Recognition = CreateRequiredRecognition()
            });

            var persisted = Assert.Single(await repository.GetMapsAsync());
            Assert.Equal((400, 200), ReadSize(repository.GetFloorImagePath(persisted, "1f")));
            Assert.Equal(2, persisted.ClassProperties.ImageDownsampleFactor);
            Assert.True(File.Exists(Path.Combine(
                root, "maps", ".downsample-originals", saved.Id.ToString("N"),
                "floor-001-visual.png")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static MapRecognitionProfile CreateRequiredRecognition()
    {
        var recognition = new MapRecognitionProfile();
        recognition.EnsureStandardAnchors();
        recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = .1, Y = .1, Width = .1, Height = .1 };
        recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = .8, Y = .8, Width = .1, Height = .1 };
        return recognition;
    }

    private static (int Width, int Height) ReadSize(string path)
    {
        using var image = Cv2.ImRead(path, ImreadModes.Unchanged);
        return (image.Width, image.Height);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
