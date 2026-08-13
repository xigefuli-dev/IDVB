using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class SideEntranceFeatureIntegrityTests
{
    [Fact]
    public async Task MissingVersionAndTamperedFeatureAreRebuiltBeforeCacheUse()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.SideFeature.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.png");
            using (var source = new Mat(320, 420, MatType.CV_8UC3, Scalar.All(120)))
            {
                Cv2.Rectangle(source, new Rect(30, 40, 210, 100), Scalar.All(35), -1);
                Cv2.Line(source, new Point(20, 250), new Point(390, 250), Scalar.All(230), 5);
                Assert.True(Cv2.ImWrite(sourcePath, source));
            }

            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle
                {
                    X = 0.15d,
                    Y = 0.20d,
                    Width = 0.05d,
                    Height = 0.08d
                };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle
                {
                    X = 0.65d,
                    Y = 0.65d,
                    Width = 0.05d,
                    Height = 0.08d
                };
            var repository = new MapRepository(Path.Combine(root, "maps"));
            await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = sourcePath,
                Floors =
                [
                    new FloorDefinition
                    {
                        Key = "1f",
                        DisplayName = "1F",
                        SortOrder = 1
                    }
                ],
                Recognition = recognition
            });

            var map = (await repository.GetMapsAsync()).Single();
            var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
            await repository.EnsureDerivedAssetsAsync([map]);
            Assert.Equal(
                SideEntranceFeaturePreprocessor.AlgorithmVersion,
                profile.SideEntranceFeatureAlgorithmVersion);
            Assert.True(repository.TryGetValidSideEntranceFeaturePath(
                map, "1f", out var featurePath, out var initialFailure),
                initialFailure);

            using (var tampered = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0)))
                Assert.True(Cv2.ImWrite(featurePath, tampered));
            Assert.False(repository.TryGetValidSideEntranceFeaturePath(
                map, "1f", out _, out var tamperedFailure));
            Assert.Contains("哈希", tamperedFailure);

            profile.SideEntranceFeatureAlgorithmVersion = string.Empty;
            await repository.EnsureDerivedAssetsAsync([map]);

            Assert.Equal(
                SideEntranceFeaturePreprocessor.AlgorithmVersion,
                profile.SideEntranceFeatureAlgorithmVersion);
            Assert.True(repository.TryGetValidSideEntranceFeaturePath(
                map, "1f", out _, out var rebuiltFailure),
                rebuiltFailure);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReferenceChoiceKeepsSimilaritySeparateFromIdentityConfidence()
    {
        var choice = new MapRecognitionChoice
        {
            Recognition = new RuntimeMapRecognition
            {
                Result = new MapRecognitionResult
                {
                    Confidence = 0d,
                    IdentityConfidence = 0d
                }
            },
            EvidenceScore = 0.91d,
            IsReferenceOnly = true,
            EvidenceLabel = "仅供参考 · 模板相似度 91%"
        };

        Assert.Equal(0.91d, choice.RawConfidence, 8);
        Assert.Equal(0d, choice.Recognition.Result.IdentityConfidence);
        Assert.True(choice.IsReferenceOnly);
        Assert.DoesNotContain("置信度", choice.EvidenceLabel);
    }
}
