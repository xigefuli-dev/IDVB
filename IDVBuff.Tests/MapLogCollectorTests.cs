using System.Text.Json;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapLogCollectorTests
{
    [Fact]
    public void SessionPathsAreUniqueWithinTheSameMillisecond()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new MapLogRepository(root);
            var first = repository.CreateSessionPath();
            var second = repository.CreateSessionPath();

            Assert.NotEqual(first, second);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task FlushesBatchesWithoutDroppingEarlierEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var collector = new MapLogCollector(new MapLogRepository(root));
            collector.IsEnabled = true;
            for (var index = 0; index < 60; index++)
            {
                collector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Info,
                    $"entry-{index}");
            }

            collector.IsEnabled = false;
            await collector.DisposeAsync();

            var path = Assert.Single(Directory.GetFiles(root, "scan-log-*.json"));
            var entries = await ReadEntriesAsync(path);
            Assert.Equal(62, entries.Count);
            Assert.Equal("Log collection started", entries[0].Message);
            Assert.Equal("entry-0", entries[1].Message);
            Assert.Equal("entry-59", entries[^2].Message);
            Assert.Equal("Log collection stopped", entries[^1].Message);
            Assert.Equal(
                entries.Count,
                entries.Select(entry => entry.Sequence).Distinct().Count());
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task RapidlyRestartedSessionsRemainSeparate()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var collector = new MapLogCollector(new MapLogRepository(root));
            collector.IsEnabled = true;
            collector.Append(MapLogCategory.System, MapLogLevel.Info, "first-session");
            collector.IsEnabled = false;

            collector.IsEnabled = true;
            collector.Append(MapLogCategory.System, MapLogLevel.Info, "second-session");
            collector.IsEnabled = false;
            await collector.DisposeAsync();

            var paths = Directory.GetFiles(root, "scan-log-*.json");
            Assert.Equal(2, paths.Length);
            var allEntries = new List<MapLogEntry>();
            foreach (var path in paths)
                allEntries.AddRange(await ReadEntriesAsync(path));

            Assert.Contains(allEntries, entry => entry.Message == "first-session");
            Assert.Contains(allEntries, entry => entry.Message == "second-session");
            Assert.Equal(2, allEntries.Count(entry => entry.Message == "Log collection started"));
            Assert.Equal(2, allEntries.Count(entry => entry.Message == "Log collection stopped"));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static async Task<List<MapLogEntry>> ReadEntriesAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<MapLogEntry>>(stream)
            ?? [];
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "IDVBuff-MapLogTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for a failed test.
        }
    }
}
