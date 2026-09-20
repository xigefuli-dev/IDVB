using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

/// <summary>
/// 常驻参考特征复用的回归测试。
/// 背景：结构缓存内存命中时 <see cref="MapStructureRegistrar"/> 根本不读
/// ReferenceImage，但调用方此前仍要先解码一张上百万像素的识别图（实测均值
/// 15ms/次）才敢调用 GetOrCreate。改为常驻命中时直接取特征、跳过解码后，
/// 请求里的 ReferenceImage 会是空 Mat——校验必须据 PreparedReference 放行。
/// </summary>
public sealed class MapStructureResidentReferenceTests
{
    [Fact]
    public void ResidentLeaseMissesBeforeFirstCreateAndSharesInstanceAfter()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.ResidentCache.{Guid.NewGuid():N}");
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

            Assert.Null(cache.TryRentResident(mapId, updatedAt, "1f"));

            using (cache.GetOrCreate(mapId, updatedAt, image, null, "1f"))
            {
            }

            using var lease = cache.TryRentResident(mapId, updatedAt, "1f");
            Assert.NotNull(lease);
            var features = lease!.Features;
            // 常驻实例必须自带配准所需的全部输入，调用方才敢跳过解码。
            Assert.False(features.Edges.Empty());
            Assert.Equal(image.Size(), features.Edges.Size());
            Assert.False(features.StructureMask.Empty());
            Assert.False(
                features.GetOrCreateClippedReferenceDistanceMap(12d).Empty());

            // 租借的价值在于零拷贝：两次借用必须是同一个实例。
            using var second = cache.TryRentResident(mapId, updatedAt, "1f");
            Assert.NotNull(second);
            Assert.Same(features, second!.Features);

            // 楼层是缓存键的一部分：别的楼层不得借用本层的常驻特征。
            Assert.Null(cache.TryRentResident(mapId, updatedAt, "2f"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EvictionWhileLeasedKeepsBorrowedFeaturesUsable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.ResidentCache.{Guid.NewGuid():N}");
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
            using var lease = cache.TryRentResident(borrowedId, updatedAt, "1f");
            Assert.NotNull(lease);

            // 灌满 LRU 并越过容量，把被租用的条目挤出去。
            for (var i = 0;
                i < MapStructureReferenceCache.MaxCacheSlots + 2;
                i++)
            {
                using (cache.GetOrCreate(
                    Guid.NewGuid(), updatedAt, image, null, "1f"))
                {
                }
            }

            // 条目已被淘汰，但仍被租用——释放必须延后，借来的 Mat 仍可用。
            Assert.False(lease!.Features.Edges.Empty());
            Assert.Equal(image.Size(), lease.Features.Edges.Size());
            Assert.Equal(
                image.Size(),
                lease.Features.GetOrCreateClippedReferenceDistanceMap(12d).Size());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreparedReferenceMakesEmptyReferenceImageValidInput()
    {
        using var live = CreateReferenceImage();
        using var prepared = new MapStructurePreprocessor().Process(live);

        var withPrepared = CreateRequest(live, prepared);
        Assert.Null(MapStructureValidator.ValidateRequest(
            withPrepared,
            usedRestrictedSearch: false));

        // 没有 PreparedReference 时 Registrar 会现场预处理 ReferenceImage，
        // 此时空参考图仍必须按 InvalidInput 拒绝。
        var withoutPrepared = CreateRequest(live, preparedReference: null);
        var rejected = MapStructureValidator.ValidateRequest(
            withoutPrepared,
            usedRestrictedSearch: false);
        Assert.NotNull(rejected);
        Assert.Equal(
            MapStructureRejectionReason.InvalidInput,
            rejected!.RejectionReason);
    }

    private static MapStructureRegistrationRequest CreateRequest(
        Mat live,
        MapStructureFeatures? preparedReference) => new()
        {
            // 常驻命中路径不解码识别图，这里刻意保持默认空 Mat。
            LiveRoi = live,
            ViewportBounds = new MapScreenRect(0d, 0d, 128d, 128d),
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = 1d,
                ScaleY = 1d,
                ReferenceWidth = 128,
                ReferenceHeight = 128,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            Tuning = new MapStructureRegistrationTuning(),
            PreparedReference = preparedReference
        };

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
            new Point(120, 100),
            Scalar.All(240),
            thickness: 2);
        return image;
    }
}
