using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapClassCacheInvalidationTests
{
    [Fact]
    public void ChangedMapDisposesItsResidentStructureGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new MapStructureReferenceCache(
                new MapStructurePreprocessor(), root);
            using var image = new Mat(
                new Size(160, 120),
                MatType.CV_8UC3,
                Scalar.White);
            var mapId = Guid.NewGuid();
            using var first = cache.GetOrCreate(
                mapId,
                DateTimeOffset.UtcNow,
                image);
            Assert.Equal(1, cache.ResidentCount);

            cache.InvalidateMaps(new HashSet<Guid> { mapId });

            Assert.Equal(0, cache.ResidentCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
