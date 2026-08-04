using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapAlignmentResearchCollectorTests
{
    [Fact]
    public void DisabledCollectorHasNoFilesystemSideEffects()
    {
        var root = TemporaryRoot();
        var collector = new MapAlignmentResearchCollector(root);

        collector.Record(new MapAlignmentResearchAttempt
        {
            MapId = Guid.NewGuid(),
            FloorKey = "upper"
        });

        Assert.False(Directory.Exists(root));
        Assert.Equal(0, collector.RecordCount);
    }

    [Fact]
    public async Task EnabledCollectorWritesJsonAndFailureArtifacts()
    {
        var root = TemporaryRoot();
        await using var collector = new MapAlignmentResearchCollector(root);
        try
        {
            await collector.SetEnabledAsync(true);
            using var frame = new Mat(new Size(80, 60), MatType.CV_8UC3, Scalar.Black);
            var attemptId = Guid.NewGuid();
            collector.Record(new MapAlignmentResearchAttempt
            {
                AttemptId = attemptId,
                MapId = Guid.NewGuid(),
                MapUpdatedAt = DateTimeOffset.UtcNow,
                FloorKey = "upper",
                FloorPosition = 2,
                FailureCategory = MapAlignmentResearchFailureCategory.NoCandidate,
                FailureReason = "no candidate"
            }, frame);
            await collector.SetEnabledAsync(false);

            var session = Assert.Single(Directory.GetDirectories(root));
            var jsonLines = await File.ReadAllLinesAsync(
                Path.Combine(session, "attempts.jsonl"));
            Assert.Single(jsonLines);
            Assert.Contains(attemptId.ToString(), jsonLines[0], StringComparison.OrdinalIgnoreCase);
            var artifactDirectory = Path.Combine(
                session,
                "artifacts",
                attemptId.ToString("N"));
            Assert.True(File.Exists(Path.Combine(artifactDirectory, "live-viewport.png")));
            Assert.True(File.Exists(Path.Combine(artifactDirectory, "structure-edges.png")));
            Assert.True(File.Exists(Path.Combine(artifactDirectory, "valid-mask.png")));
            Assert.True(File.Exists(Path.Combine(artifactDirectory, "candidate-overlay.png")));
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
        var oldSession = Path.Combine(root, "old-session");
        Directory.CreateDirectory(oldSession);
        await File.WriteAllBytesAsync(Path.Combine(oldSession, "data.bin"), new byte[32]);
        Directory.SetCreationTimeUtc(oldSession, DateTime.UtcNow.AddDays(-1));
        await using var collector = new MapAlignmentResearchCollector(
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

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"IDVBuff.AlignmentResearch.{Guid.NewGuid():N}");
}
