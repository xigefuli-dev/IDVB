using IDVBuff.Features.Maps;
using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapStructureReferenceCacheTests
{
    [Fact]
    public void SameMapDifferentFloorsGetDistinctDiskCacheDirectories()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.StructureCache.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mapId = Guid.NewGuid();
            var updatedAt = new DateTimeOffset(
                2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var image = CreateReferenceImage();
            using var cache = new MapStructureReferenceCache(
                new MapStructurePreprocessor(),
                root);

            using (cache.GetOrCreate(mapId, updatedAt, image, null, "1f"))
            {
            }
            using (cache.GetOrCreate(mapId, updatedAt, image, null, "2f"))
            {
            }

            var version = MapStructurePreprocessor.AlgorithmVersion;
            var generationFingerprint =
                new MapStructureGenerationTuning().CacheFingerprint;
            var profile = MapStructurePreprocessingProfile.EdgesAndFeatures;
            var mapDirectory = Path.Combine(root, mapId.ToString("N"));
            var firstDirectory = Path.Combine(
                mapDirectory,
                $"{updatedAt.UtcTicks}-1f-{version}-{generationFingerprint}-{profile}");
            var secondDirectory = Path.Combine(
                mapDirectory,
                $"{updatedAt.UtcTicks}-2f-{version}-{generationFingerprint}-{profile}");

            Assert.True(Directory.Exists(firstDirectory));
            Assert.True(Directory.Exists(secondDirectory));
            Assert.True(File.Exists(
                Path.Combine(firstDirectory, "structure-mask.png")));
            Assert.True(File.Exists(
                Path.Combine(secondDirectory, "structure-mask.png")));
            Assert.Equal("1f", ReadFloor(firstDirectory));
            Assert.Equal("2f", ReadFloor(secondDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static Mat CreateReferenceImage()
    {
        var image = new Mat(
            new Size(128, 128),
            MatType.CV_8UC1,
            Scalar.All(96));
        Cv2.Rectangle(
            image,
            new Rect(16, 16, 48, 32),
            Scalar.All(220),
            thickness: -1);
        Cv2.Line(
            image,
            new Point(8, 100),
            new Point(120, 60),
            Scalar.All(255),
            thickness: 2);
        return image;
    }

    private static string ReadFloor(string directory)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "metadata.json")));
        return document.RootElement.GetProperty("Floor").GetString() ?? string.Empty;
    }
}
