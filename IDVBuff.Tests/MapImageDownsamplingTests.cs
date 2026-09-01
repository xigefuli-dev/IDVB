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
