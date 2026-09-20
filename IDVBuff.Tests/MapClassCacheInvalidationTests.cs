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

    [Fact]
    public void HaveSameFingerprintInputs_DetectsClassChange()
    {
        var left = CreateSampleMap(className: "ClassA");
        var right = CreateSampleMap(className: "ClassB");
        right.Id = left.Id;
        right.UpdatedAt = left.UpdatedAt;

        Assert.False(MapCvRecognitionHelpers.HaveSameFingerprintInputs(left, right));
    }

    [Fact]
    public void HaveSameFingerprintInputs_DetectsPrebuiltStructureLineChange()
    {
        var left = CreateSampleMap(prebuiltSha: "sha1");
        var right = CreateSampleMap(prebuiltSha: "sha2");
        right.Id = left.Id;
        right.UpdatedAt = left.UpdatedAt;

        Assert.False(MapCvRecognitionHelpers.HaveSameFingerprintInputs(left, right));
    }

    [Fact]
    public void HaveSameFingerprintInputs_DetectsSideEntranceFeatureChange()
    {
        var left = CreateSampleMap(sideEntranceSha: "side_sha1");
        var right = CreateSampleMap(sideEntranceSha: "side_sha2");
        right.Id = left.Id;
        right.UpdatedAt = left.UpdatedAt;

        Assert.False(MapCvRecognitionHelpers.HaveSameFingerprintInputs(left, right));
    }

    [Fact]
    public void HaveSameFingerprintInputs_ReturnsTrueForIdenticalInputs()
    {
        var left = CreateSampleMap();
        var right = CreateSampleMap();
        right.Id = left.Id;
        right.UpdatedAt = left.UpdatedAt;

        Assert.True(MapCvRecognitionHelpers.HaveSameFingerprintInputs(left, right));
    }

    private static MapRecord CreateSampleMap(
        string className = "ClassA",
        string prebuiltSha = "sha_prebuilt_1",
        string sideEntranceSha = "sha_side_1")
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var profile = new FloorRecognitionProfile
        {
            FloorKey = "1f",
            SideEntranceFeatureSha256 = sideEntranceSha,
            SideEntranceFeatureAlgorithmVersion = "v1"
        };
        return new MapRecord
        {
            Id = id,
            Class = className,
            UpdatedAt = now,
            Floors =
            [
                new FloorDefinition
                {
                    Key = "1f",
                    DisplayName = "1F",
                    SortOrder = 1,
                    ImageSha256 = "img_sha",
                    RecognitionSha256 = "rec_sha",
                    OverlaySha256 = "ovl_sha",
                    ImageFileLength = 100,
                    RecognitionFileLength = 100,
                    OverlayFileLength = 100,
                    ImageLastWriteUtcTicks = 1000,
                    RecognitionLastWriteUtcTicks = 1000,
                    OverlayLastWriteUtcTicks = 1000,
                    PrebuiltStructureLine = new PrebuiltStructureLineAsset
                    {
                        Sha256 = prebuiltSha,
                        AlgorithmSha256 = "algo_sha"
                    }
                }
            ],
            Recognition = new MapRecognitionProfile
            {
                Floors = new Dictionary<string, FloorRecognitionProfile>
                {
                    ["1f"] = profile
                },
                FirstFloor = profile
            }
        };
    }
}
