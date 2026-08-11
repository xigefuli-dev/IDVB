using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapAlignmentResearchCollectorTests
{
    [Fact]
    public void DisabledCollectorHasNoFilesystemSideEffects()
    {
        var root = TemporaryRoot();
        var collector = new MapAlignmentResearchCollector(
            preprocessor: null, root);

        var map = CreateMap();
        using var frame = new Mat(
            new Size(40, 30), MatType.CV_8UC3, Scalar.Black);
        collector.RecordAttempt(
            new MapAlignmentResearchAttempt
            {
                MapId = map.Id,
                FloorKey = "1f"
            },
            map,
            "1f",
            frame);

        Assert.False(Directory.Exists(root));
        Assert.Equal(0, collector.RecordCount);
    }

    [Fact]
    public async Task EnabledCollectorWritesJsonAndFailureArtifacts()
    {
        var root = TemporaryRoot();
        await using var collector = new MapAlignmentResearchCollector(
            preprocessor: null, root);
        try
        {
            await collector.SetEnabledAsync(true);
            Assert.True(collector.IsEnabled);
            using var frame = new Mat(
                new Size(80, 60), MatType.CV_8UC3, Scalar.Black);
            var attemptId = Guid.NewGuid();
            var map = CreateMap();
            collector.RecordAttempt(
                new MapAlignmentResearchAttempt
                {
                    AttemptId = attemptId,
                    MapId = map.Id,
                    MapUpdatedAt = DateTimeOffset.UtcNow,
                    FloorKey = "1f",
                    FloorPosition = 1,
                    Accepted = false,
                    FailureCategory = MapAlignmentResearchFailureCategory.NoCandidate,
                    FailureReason = "no candidate"
                },
                map,
                "1f",
                frame);
            await collector.SetEnabledAsync(false);
            Assert.False(collector.IsEnabled);

            var sessionsDir = Path.Combine(root, "sessions");
            Assert.True(Directory.Exists(sessionsDir));
            var session = Assert.Single(Directory.GetDirectories(sessionsDir));

            var jsonContent = await File.ReadAllTextAsync(
                Path.Combine(session, "attempts.jsonl"));
            Assert.Contains(
                attemptId.ToString(),
                jsonContent,
                StringComparison.OrdinalIgnoreCase);

            // 新目录结构：{map-short}/{floor}/{seq}-{outcome}/
            var mapShort = map.Id.ToString("N")[..8];
            var caseParent = Path.Combine(session, mapShort, "1f");
            Assert.True(Directory.Exists(caseParent));
            var caseDirs = Directory.GetDirectories(caseParent);
            Assert.Single(caseDirs);

            // 失败案例应保存 4 张图
            Assert.True(File.Exists(Path.Combine(caseDirs[0], "viewport.png")));
            Assert.True(File.Exists(Path.Combine(caseDirs[0], "edges.png")));
            Assert.True(File.Exists(Path.Combine(caseDirs[0], "valid-mask.png")));
            Assert.True(File.Exists(Path.Combine(caseDirs[0], "overlay.png")));
            Assert.True(File.Exists(Path.Combine(caseDirs[0], "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisableFlushesAttemptsAndReenableStartsAnotherSession()
    {
        var root = TemporaryRoot();
        await using var collector = new MapAlignmentResearchCollector(
            preprocessor: null, root);
        try
        {
            await collector.SetEnabledAsync(true);
            var firstSession = collector.CurrentSessionDirectory;
            Assert.NotNull(firstSession);

            var attemptId = Guid.NewGuid();
            var map = CreateMap();
            using var frame = new Mat(
                new Size(40, 30), MatType.CV_8UC3, Scalar.Black);
            collector.RecordAttempt(
                new MapAlignmentResearchAttempt
                {
                    AttemptId = attemptId,
                    MapId = map.Id,
                    MapUpdatedAt = map.UpdatedAt,
                    FloorKey = "1f",
                    Accepted = true,
                    Confidence = 0.8d
                },
                map,
                "1f",
                frame);

            await collector.SetEnabledAsync(false);

            Assert.False(collector.IsEnabled);
            Assert.NotNull(firstSession);
            var attemptsPath = Path.Combine(firstSession!, "attempts.jsonl");
            Assert.Contains(
                attemptId.ToString(),
                await File.ReadAllTextAsync(attemptsPath),
                StringComparison.OrdinalIgnoreCase);

            await collector.SetEnabledAsync(true);
            Assert.True(collector.IsEnabled);
            Assert.NotEqual(firstSession, collector.CurrentSessionDirectory);
            await collector.SetEnabledAsync(false);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AttemptFactoryPersistsSuccessfulAndFailedAttempts()
    {
        var root = TemporaryRoot();
        await using var collector = new MapAlignmentResearchCollector(
            preprocessor: null, root);
        try
        {
            await collector.SetEnabledAsync(true);
            var map = CreateMap();
            var signature = new MapWindowSignature
            {
                ClientWidth = 1280,
                ClientHeight = 720,
                ViewportWidth = 1280,
                ViewportHeight = 720
            };
            var settings = new MapRuntimeSettings();
            var session = new MapSessionSnapshot
            {
                Version = 7,
                AlignmentRevision = 9
            };
            var successId = Guid.NewGuid();
            var failureId = Guid.NewGuid();
            var success = MapAlignmentResearchAttemptFactory.Create(
                map,
                "1f",
                new MapRecognitionAttempt
                {
                    Recognition = new RuntimeMapRecognition
                    {
                        Map = map,
                        Result = new MapRecognitionResult
                        {
                            Floor = "1f",
                            Confidence = 0.9d,
                            OverlayTransform = new MapOverlayTransform
                            {
                                ScaleX = 1d,
                                ScaleY = 1d,
                                ReferenceWidth = 200,
                                ReferenceHeight = 150
                            }
                        }
                    }
                },
                settings,
                session,
                signature,
                "initial-scan") with { AttemptId = successId };
            var failure = MapAlignmentResearchAttemptFactory.Create(
                map,
                "1f",
                new MapRecognitionAttempt
                {
                    FailureReason = "no candidate"
                },
                settings,
                session,
                signature,
                "initial-scan") with { AttemptId = failureId };

            using var frame = new Mat(
                new Size(80, 60), MatType.CV_8UC3, Scalar.Black);
            collector.RecordAttempt(success, map, "1f", frame);
            collector.RecordAttempt(failure, map, "1f", frame);
            await collector.SetEnabledAsync(false);

            var sessionDirectory = Assert.Single(
                Directory.GetDirectories(Path.Combine(root, "sessions")));
            var attempts = await File.ReadAllTextAsync(
                Path.Combine(sessionDirectory, "attempts.jsonl"));
            Assert.Contains(successId.ToString(), attempts);
            Assert.Contains(failureId.ToString(), attempts);
            Assert.Equal(
                2,
                Directory.GetDirectories(
                    Path.Combine(sessionDirectory, map.Id.ToString("N")[..8], "1f")).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupRemovesOldestSessionWhenSizeLimitIsExceeded()
    {
        var root = TemporaryRoot();
        var sessionsRoot = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        var oldSession = Path.Combine(sessionsRoot, "2020-01-01_000000--deadbeef");
        Directory.CreateDirectory(oldSession);
        await File.WriteAllBytesAsync(
            Path.Combine(oldSession, "data.bin"), new byte[32]);
        Directory.SetCreationTimeUtc(oldSession, DateTime.UtcNow.AddDays(-1));
        await using var collector = new MapAlignmentResearchCollector(
            preprocessor: null,
            root,
            retention: TimeSpan.FromDays(30),
            maximumBytes: 8);
        try
        {
            await collector.SetEnabledAsync(true);
            Assert.False(Directory.Exists(oldSession));
            await collector.SetEnabledAsync(false);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CacheReferenceImage_SavesReferenceAndDerivedImages()
    {
        var root = TemporaryRoot();
        try
        {
            var collector = new MapAlignmentResearchCollector(
                new MapStructurePreprocessor(), root);
            collector.SetEnabledAsync(true).GetAwaiter().GetResult();

            // 创建一张小型合成参考图
            var refPath = Path.Combine(root, "test-ref.png");
            using (var refImg = new Mat(
                new Size(200, 150), MatType.CV_8UC3, Scalar.Gray))
            {
                Cv2.Rectangle(refImg, new Rect(30, 30, 80, 50),
                    Scalar.White, -1);
                Cv2.ImWrite(refPath, refImg);
            }

            var mapId = Guid.NewGuid();
            collector.CacheReferenceImage(refPath, mapId, "1f");

            collector.SetEnabledAsync(false).GetAwaiter().GetResult();

            var sessionsDir = Path.Combine(root, "sessions");
            var session = Assert.Single(Directory.GetDirectories(sessionsDir));
            var mapShort = mapId.ToString("N")[..8];
            var refDir = Path.Combine(session, mapShort, "1f");

            Assert.True(File.Exists(Path.Combine(refDir, "reference.png")));
            Assert.True(File.Exists(Path.Combine(refDir, "reference-edges.png")));
            Assert.True(File.Exists(Path.Combine(refDir, "reference-structure.png")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MapRecord CreateMap() => new()
    {
        Id = Guid.NewGuid(),
        UpdatedAt = DateTimeOffset.UtcNow,
        Title = "Test Map",
        Floors =
        [
            new FloorDefinition
            {
                Key = "1f", DisplayName = "1F", SortOrder = 1,
                ImageWidth = 200, ImageHeight = 150
            }
        ],
        Recognition = new MapRecognitionProfile
        {
            FirstFloor = new FloorRecognitionProfile
            {
                FloorKey = "1f",
                RecognitionPixelWidth = 200,
                RecognitionPixelHeight = 150
            }
        }
    };

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"IDVBuff.AlignmentResearch.{Guid.NewGuid():N}");
}
