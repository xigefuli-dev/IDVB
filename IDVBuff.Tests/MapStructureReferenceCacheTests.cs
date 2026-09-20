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

    [Fact]
    public void Clear_DisposesUnleasedEntriesAndEmptiesMemoryCache()
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

            Assert.Equal(1, cache.ResidentCount);

            cache.Clear();

            Assert.Equal(0, cache.ResidentCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Clear_DisposesEvictedWhileLeasedAndResetsAllAccounting()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.StructureCache.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var updatedAt = new DateTimeOffset(
                2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var image = CreateReferenceImage();
            using var cache = new MapStructureReferenceCache(
                new MapStructurePreprocessor(),
                root);

            var borrowedId = Guid.NewGuid();
            using (cache.GetOrCreate(borrowedId, updatedAt, image, null, "1f"))
            {
            }

            var lease = cache.TryRentResident(borrowedId, updatedAt, "1f");
            Assert.NotNull(lease);

            // 灌满 LRU，将 borrowedId 挤入 _evictedWhileLeased
            for (var i = 0; i < MapStructureReferenceCache.MaxCacheSlots + 2; i++)
            {
                using (cache.GetOrCreate(Guid.NewGuid(), updatedAt, image, null, "1f"))
                {
                }
            }

            // 此时调用 Clear()，必须将 _memoryCache 与 _evictedWhileLeased 一并彻底清空并释放底层资源
            cache.Clear();

            Assert.Equal(0, cache.ResidentCount);
            Assert.Null(cache.TryRentResident(borrowedId, updatedAt, "1f"));

            // 归还已失效的 lease 不应抛出异常
            lease!.Dispose();

            // 重新填入同 key 条目，借用计数应从干净的 0 开始，正常租借与归还
            using (cache.GetOrCreate(borrowedId, updatedAt, image, null, "1f"))
            {
            }
            Assert.Equal(1, cache.ResidentCount);
            using var freshLease = cache.TryRentResident(borrowedId, updatedAt, "1f");
            Assert.NotNull(freshLease);
            Assert.False(freshLease!.Features.Edges.Empty());
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
