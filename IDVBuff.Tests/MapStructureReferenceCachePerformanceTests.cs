using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Features.Maps.Tests;

public class MapStructureReferenceCachePerformanceTests
{
    private readonly ITestOutputHelper _output;

    public MapStructureReferenceCachePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void MultiFloorCache_ShouldHandleFrequentSwitching()
    {
        // 模拟频繁在一楼和二楼之间切换的场景
        var preprocessor = new MapStructurePreprocessor();
        using var cache = new MapStructureReferenceCache(
            preprocessor,
            Path.Combine(Path.GetTempPath(), "idvbuff-cache-test"));

        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;

        // 创建模拟的一楼和二楼图片
        using var floor1Image = new Mat(835, 1169, MatType.CV_8UC3, Scalar.All(128));
        using var floor2Image = new Mat(861, 1187, MatType.CV_8UC3, Scalar.All(140));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 第一轮：冷启动，加载一楼和二楼
        _output.WriteLine("=== 第一轮：冷启动 ===");
        stopwatch.Restart();
        using (var f1 = cache.GetOrCreate(mapId, updatedAt, floor1Image, floor: "1f"))
        {
            stopwatch.Stop();
            _output.WriteLine($"一楼首次加载: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.NotNull(f1);
        }

        stopwatch.Restart();
        using (var f2 = cache.GetOrCreate(mapId, updatedAt, floor2Image, floor: "2f"))
        {
            stopwatch.Stop();
            _output.WriteLine($"二楼首次加载: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.NotNull(f2);
        }

        // 第二轮：频繁切换，应该全部命中缓存
        _output.WriteLine("\n=== 第二轮：频繁切换（应全部命中缓存）===");
        var floor1Times = new List<double>();
        var floor2Times = new List<double>();

        for (int i = 0; i < 10; i++)
        {
            // 访问一楼
            stopwatch.Restart();
            using (var f1 = cache.GetOrCreate(mapId, updatedAt, floor1Image, floor: "1f"))
            {
                stopwatch.Stop();
                floor1Times.Add(stopwatch.Elapsed.TotalMilliseconds);
                Assert.NotNull(f1);
            }

            // 访问二楼
            stopwatch.Restart();
            using (var f2 = cache.GetOrCreate(mapId, updatedAt, floor2Image, floor: "2f"))
            {
                stopwatch.Stop();
                floor2Times.Add(stopwatch.Elapsed.TotalMilliseconds);
                Assert.NotNull(f2);
            }
        }

        var avg1f = floor1Times.Average();
        var avg2f = floor2Times.Average();

        _output.WriteLine($"\n一楼平均缓存命中耗时: {avg1f:F1} ms（{floor1Times.Count} 次）");
        _output.WriteLine($"二楼平均缓存命中耗时: {avg2f:F1} ms（{floor2Times.Count} 次）");
        _output.WriteLine($"性能差距: {Math.Abs(avg2f - avg1f):F1} ms");

        // 验证：缓存命中应该非常快（< 20ms，包含 Clone 开销），且一楼和二楼耗时相近
        Assert.True(avg1f < 20, $"一楼缓存命中应 < 20ms，实际 {avg1f:F1} ms");
        Assert.True(avg2f < 20, $"二楼缓存命中应 < 20ms，实际 {avg2f:F1} ms");

        var ratio = Math.Max(avg1f, avg2f) / Math.Min(avg1f, avg2f);
        Assert.True(ratio < 2.0, $"一楼和二楼耗时应接近，实际差距 {ratio:F2}x");

        _output.WriteLine($"\n✓ 测试通过：缓存命中性能良好，一楼和二楼耗时比 {ratio:F2}x");
    }

    [Fact]
    public void Cache_ShouldSupportMultipleMaps()
    {
        // 验证缓存支持多地图多楼层（8 槽容量）
        var preprocessor = new MapStructurePreprocessor();
        using var cache = new MapStructureReferenceCache(
            preprocessor,
            Path.Combine(Path.GetTempPath(), "idvbuff-cache-test-multi"));

        var maps = new[]
        {
            (Guid.NewGuid(), "地图1"),
            (Guid.NewGuid(), "地图2"),
            (Guid.NewGuid(), "地图3"),
            (Guid.NewGuid(), "地图4")
        };

        var updatedAt = DateTimeOffset.UtcNow;
        using var testImage = new Mat(800, 1000, MatType.CV_8UC3, Scalar.All(128));

        _output.WriteLine("=== 加载 4 地图 × 2 楼层 = 8 个缓存条目 ===");

        // 加载所有地图的一楼和二楼
        foreach (var (mapId, name) in maps)
        {
            using var f1 = cache.GetOrCreate(mapId, updatedAt, testImage, floor: "1f");
            using var f2 = cache.GetOrCreate(mapId, updatedAt, testImage, floor: "2f");
            _output.WriteLine($"已加载 {name} 的一楼和二楼");
        }

        _output.WriteLine("\n=== 重新访问所有条目，验证缓存 ===");

        // 重新访问所有条目，应该都命中缓存
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (mapId, name) in maps)
        {
            stopwatch.Restart();
            using (var f1 = cache.GetOrCreate(mapId, updatedAt, testImage, floor: "1f"))
            {
                stopwatch.Stop();
                var time1 = stopwatch.Elapsed.TotalMilliseconds;

                stopwatch.Restart();
                using var f2 = cache.GetOrCreate(mapId, updatedAt, testImage, floor: "2f");
                stopwatch.Stop();
                var time2 = stopwatch.Elapsed.TotalMilliseconds;

                _output.WriteLine($"{name}: 1F={time1:F1}ms, 2F={time2:F1}ms");

                // 应该都是缓存命中（< 20ms，包含 Clone 开销）
                Assert.True(time1 < 20, $"{name} 一楼应命中缓存，实际 {time1:F1} ms");
                Assert.True(time2 < 20, $"{name} 二楼应命中缓存，实际 {time2:F1} ms");
            }
        }

        _output.WriteLine("\n✓ 测试通过：8 槽缓存支持多地图多楼层");
    }

    [Fact]
    public void Cache_ShouldEvictLRU_WhenFull()
    {
        // 验证当缓存满时，应该驱逐最旧的条目（LRU）
        var preprocessor = new MapStructurePreprocessor();
        using var cache = new MapStructureReferenceCache(
            preprocessor,
            Path.Combine(Path.GetTempPath(), "idvbuff-cache-test-lru"));

        var updatedAt = DateTimeOffset.UtcNow;
        using var testImage = new Mat(800, 1000, MatType.CV_8UC3, Scalar.All(128));

        _output.WriteLine("=== 填满缓存（8 槽）===");

        // 创建 8 个不同的缓存条目（填满缓存）
        var maps = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var mapId in maps)
        {
            using var f = cache.GetOrCreate(mapId, updatedAt, testImage, floor: "1f");
            _output.WriteLine($"已加载地图 {mapId:N} 一楼");
        }
        var afterFill = cache.GetStatisticsForDiagnostics();
        Assert.Equal(0, afterFill.Hits);
        Assert.Equal(8, afterFill.Misses);

        _output.WriteLine("\n=== 访问第 9 个条目，触发 LRU 驱逐 ===");

        // 添加第 9 个条目，应该驱逐最旧的（maps[0]）
        var map9 = Guid.NewGuid();
        using (var f = cache.GetOrCreate(map9, updatedAt, testImage, floor: "1f"))
        {
            _output.WriteLine($"已加载地图 {map9:N} 一楼（第 9 个）");
        }
        var afterNinth = cache.GetStatisticsForDiagnostics();
        Assert.Equal(0, afterNinth.Hits);
        Assert.Equal(9, afterNinth.Misses);

        _output.WriteLine("\n=== 重新访问前 8 个条目 ===");

        // 先验证 maps[1] 命中；如果先重新加载已驱逐的 maps[0]，LRU
        // 会正常再驱逐一个旧条目，随后检查 maps[1] 就不再能证明第九次
        // 插入的驱逐结果。使用命中/未命中计数也避免依赖机器速度。
        using (var f = cache.GetOrCreate(maps[1], updatedAt, testImage, floor: "1f"))
        {
            var afterResidentAccess = cache.GetStatisticsForDiagnostics();
            Assert.Equal(1, afterResidentAccess.Hits);
            Assert.Equal(9, afterResidentAccess.Misses);
            _output.WriteLine("地图 1: 内存缓存命中");
        }

        using (var f = cache.GetOrCreate(maps[0], updatedAt, testImage, floor: "1f"))
        {
            var afterEvictedAccess = cache.GetStatisticsForDiagnostics();
            Assert.Equal(1, afterEvictedAccess.Hits);
            Assert.Equal(10, afterEvictedAccess.Misses);
            _output.WriteLine("地图 0: 已被驱逐，重新加载");
        }

        _output.WriteLine("\n✓ 测试通过：LRU 驱逐机制正常");
    }
}
