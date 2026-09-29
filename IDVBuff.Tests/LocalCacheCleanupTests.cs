using System.Text;
using System.Text.Json;
using IDVBuff.Diagnostics;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed class LocalCacheCleanupTests(ITestOutputHelper output)
{
    [Fact]
    public void UnitTestsUseProcessTemporaryDataWithoutMigratingUserData()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), $"IDVB-UnitTests-{Environment.ProcessId}"), AppDataPaths.RootDirectory);
    }

    [Fact]
    public void ScanIsReadOnlyAndExecutePreservesMapsSettingsKeysAndCurrentPackage()
    {
        using var data = new Data();
        var cache = data.Cache();
        var snapshot = data.Write("AlignmentResearch/scan-snapshots-20260921/formal-one/maps.json");
        var map = data.Write("Maps/maps.json");
        var setting = data.Write("MapRuntime/map-feature-cache.json");
        var key = data.Write("MapPublishing/keys/publisher.json");
        var unknown = data.Write("Logs/personal.txt");
        var backup = data.Write("Maps-backup-before-restore/maps.json");
        var (old, current) = data.Publication();
        var plan = LocalCacheCleanup.Scan(data.Root);
        Assert.Equal(3, plan.FileCount);
        Assert.True(File.Exists(cache));
        Assert.True(File.Exists(snapshot));
        Assert.Equal(new FileInfo(cache).Length + new FileInfo(snapshot).Length + new FileInfo(old).Length, plan.Bytes);
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Empty(result.Failures);
        Assert.Equal(plan.Bytes, result.Bytes);
        Assert.All(new[] { cache, snapshot, old }, path => Assert.False(File.Exists(path)));
        Assert.All(new[] { map, setting, key, current, unknown, backup }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void ConfirmationSnapshotSkipsChangedEntriesAndDoesNotDeleteNewFiles()
    {
        using var data = new Data();
        var changed = data.Cache();
        var plan = LocalCacheCleanup.Scan(data.Root);
        File.AppendAllText(changed, "new contents");
        var added = data.Write(Path.GetRelativePath(data.Root, Path.Combine(Path.GetDirectoryName(changed)!, "edges.png")));
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(added));
    }

    [Fact]
    public void ActiveWriterRegisteredAfterScanIsProtectedAndLeaseIsReferenceCounted()
    {
        using var data = new Data();
        var cache = data.Cache();
        var plan = LocalCacheCleanup.Scan(data.Root);
        using var first = AppDataPaths.ProtectCachePath(Path.GetDirectoryName(cache)!);
        var second = AppDataPaths.ProtectCachePath(Path.GetDirectoryName(cache)!);
        second.Dispose();
        second.Dispose();
        Assert.Empty(LocalCacheCleanup.Scan(data.Root).Entries);
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Equal(1, result.SkippedFiles);
        Assert.True(File.Exists(cache));
    }

    [Fact]
    public void AutomaticCleanupExpiresSnapshotsAndBoundsInactiveDiagnostics()
    {
        using var data = new Data();
        var snapshot = data.Write("AlignmentResearch/scan-snapshots-20260921/formal-one/maps.json", ageDays: 8);
        var oldest = data.Write("诊断模式/对局 1/image.png", ageDays: 3);
        var newest = data.Write("诊断模式/对局 2/image.png", ageDays: 1);
        var active = data.Write("诊断模式/对局 3/image.png", ageDays: 9);
        string[] Protected() => [Path.GetDirectoryName(active)!];
        var plan = LocalCacheCleanup.Scan(data.Root, automatic: true, activePaths: Protected, categoryBudget: 4);
        Assert.Contains(plan.Entries, entry => entry.Files.Any(file => file.Path == snapshot));
        Assert.Contains(plan.Entries, entry => entry.Files.Any(file => file.Path == oldest));
        Assert.DoesNotContain(plan.Entries, entry => entry.Files.Any(file => file.Path == newest || file.Path == active));
        LocalCacheCleanup.Execute(plan, Protected);
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void AutomaticCleanupRemovesOrphanAndOldRevisionButRetainsBothCurrentFloors()
    {
        using var data = new Data();
        var id = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow;
        data.Write("Maps/maps.json", JsonSerializer.Serialize(new { Maps = new[] { new { Id = id, UpdatedAt = time } } }));
        var current = data.Cache(id, time.UtcTicks, "1f");
        var secondFloor = data.Cache(id, time.UtcTicks, "2f");
        var oldRevision = data.Cache(id, time.UtcTicks - 1);
        var orphan = data.Cache();
        var plan = LocalCacheCleanup.Scan(data.Root, automatic: true);
        LocalCacheCleanup.Execute(plan);
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(secondFloor));
        Assert.False(File.Exists(oldRevision));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void MalformedCatalogAndPublicationFeedDoNotMeanEverythingIsObsolete()
    {
        using var data = new Data();
        var cache = data.Cache();
        data.Write("Maps/maps.json", "invalid");
        var (old, _) = data.Publication();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(old)!, "feed.json"), "invalid");
        Assert.Empty(LocalCacheCleanup.Scan(data.Root, automatic: true).Entries);
        Assert.True(File.Exists(cache));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void PublicationFeedChangedAfterConfirmationProtectsNewCurrentPackage()
    {
        using var data = new Data();
        var (old, _) = data.Publication();
        var plan = LocalCacheCleanup.Scan(data.Root);
        data.Feed(Path.GetDirectoryName(old)!, Path.GetFileName(old));
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Equal(1, result.SkippedFiles);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void LockedFilesReportFailureInsteadOfClaimingFreedSpace()
    {
        using var data = new Data();
        var cache = data.Cache();
        var plan = LocalCacheCleanup.Scan(data.Root);
        using var stream = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = LocalCacheCleanup.Execute(plan);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(0, result.Bytes);
    }

    [Fact]
    public void ExecuteRejectsPathsOutsideRoot()
    {
        using var data = new Data();
        using var outside = new Data();
        var cache = outside.Cache();
        var outsidePlan = LocalCacheCleanup.Scan(outside.Root);
        var result = LocalCacheCleanup.Execute(outsidePlan with { Root = data.Root });
        Assert.NotEmpty(result.Failures);
        Assert.True(File.Exists(cache));
    }

    [Fact]
    public void UnknownFilesPreventDeletionOfWholeCacheDirectory()
    {
        using var data = new Data();
        var cache = data.Cache();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(cache)!, "personal.txt"), "keep");
        Assert.Empty(LocalCacheCleanup.Scan(data.Root).Entries);
    }

    [Fact]
    public void QuotaDiscardsObsoleteEntriesBeforeEvictingValidCache()
    {
        using var data = new Data();
        var id = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow;
        data.Write("Maps/maps.json", JsonSerializer.Serialize(new { Maps = new[] { new { Id = id, UpdatedAt = time } } }));
        var valid = data.Cache(id, time.UtcTicks);
        File.SetLastWriteTimeUtc(valid, DateTime.UtcNow.AddDays(-2));
        var orphan = data.Cache();
        var plan = LocalCacheCleanup.Scan(data.Root, automatic: true, categoryBudget: 4);
        Assert.Single(plan.Entries);
        Assert.Equal(orphan, plan.Entries[0].Files[0].Path);
    }

    [Fact]
    public void OldDirectoryWithRecentWritesIsNotExpired()
    {
        using var data = new Data();
        var recent = data.Write("诊断模式/对局 1/image.png");
        Directory.SetCreationTimeUtc(Path.GetDirectoryName(recent)!, DateTime.UtcNow.AddDays(-20));
        Assert.Empty(LocalCacheCleanup.Scan(data.Root, automatic: true).Entries);
    }

    [Fact]
    public void SnapshotDirectoryAlreadyRemovedIsNotAFailureOrAnActiveFile()
    {
        using var data = new Data();
        var snapshot = data.Write("AlignmentResearch/scan-snapshots-20260921/formal-one/maps.json");
        var plan = LocalCacheCleanup.Scan(data.Root);
        Directory.Delete(Path.Combine(data.Root, "AlignmentResearch", "scan-snapshots-20260921"), true);
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, result.Bytes);
        Assert.Equal(1, result.AlreadyMissingFiles);
        Assert.Equal(plan.Bytes, result.AlreadyMissingBytes);
    }

    [Fact]
    public void RepeatedExecutionDoesNotCountFreedSpaceTwice()
    {
        using var data = new Data();
        data.Cache();
        data.Write("Logs/startup-old.log");
        var plan = LocalCacheCleanup.Scan(data.Root);
        var first = LocalCacheCleanup.Execute(plan);
        var second = LocalCacheCleanup.Execute(plan);
        Assert.Equal(plan.Bytes, first.Bytes);
        Assert.Equal(0, second.Bytes);
        Assert.Equal(plan.FileCount, second.AlreadyMissingFiles);
        Assert.Equal(0, second.SkippedFiles);
        Assert.Empty(second.Failures);
    }

    [Fact]
    public void PartlyRemovedSnapshotStillCleansUnchangedRemainingFiles()
    {
        using var data = new Data();
        var first = data.Write("AlignmentResearch/scan-snapshots-20260921/formal-one/maps.json");
        var second = data.Write("AlignmentResearch/scan-snapshots-20260921/formal-two/maps.json");
        var plan = LocalCacheCleanup.Scan(data.Root);
        File.Delete(first);
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Empty(result.Failures);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.AlreadyMissingFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(plan.Bytes, result.Bytes + result.AlreadyMissingBytes);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task ManualPreviewAndCancellationDoNotAllowAutomaticCleanup()
    {
        using var data = new Data();
        var old = data.Write("Logs/startup-old.log", ageDays: 8);
        using (await LocalCacheCleanup.BeginManualAsync())
        {
            var plan = LocalCacheCleanup.Scan(data.Root);
            Assert.Single(plan.Entries);
            Assert.Null(LocalCacheCleanup.TryBeginAutomatic());
            SavedDiagnosticDataRetention.Cleanup(data.Root, DateTime.UtcNow);
            Assert.True(File.Exists(old));
            // Cancellation: release the preview without executing the plan.
        }
        Assert.True(File.Exists(old));
        using var next = LocalCacheCleanup.TryBeginAutomatic();
        Assert.NotNull(next);
    }

    [Fact]
    public void RunningStartupWriterDoesNotBlockCleanupOfOtherFiles()
    {
        using var data = new Data();
        var startup = data.Write("Logs/startup-20260929-122631-937-41980.log");
        var cache = data.Cache();
        using var protection = AppDataPaths.ProtectCachePath(startup);
        using var writer = new FileStream(startup, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var plan = LocalCacheCleanup.Scan(data.Root);
        Assert.Single(plan.Entries);
        var result = LocalCacheCleanup.Execute(plan);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.SkippedFiles);
        Assert.True(File.Exists(startup));
        Assert.False(File.Exists(cache));
    }

    [Fact]
    public void ScanCanAuditAnExistingDirectoryWithoutExecutingCleanup()
    {
        using var data = new Data();
        data.Cache();
        var root = Environment.GetEnvironmentVariable("IDVB_CACHE_READONLY_AUDIT_ROOT") ?? data.Root;
        var plan = LocalCacheCleanup.Scan(root);
        output.WriteLine($"Read-only plan: {plan.FileCount} files / {plan.Bytes} bytes");
        foreach (var group in plan.Entries.GroupBy(entry => entry.Category))
            output.WriteLine($"{group.Key}: {group.Sum(entry => entry.Bytes)} bytes");
        Assert.All(plan.Entries.SelectMany(entry => entry.Files), file => Assert.True(File.Exists(file.Path)));
    }

    private sealed class Data : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"IDVB-Cleanup-{Guid.NewGuid():N}");
        internal Data() => Directory.CreateDirectory(Root);
        internal string Write(string relative, string content = "data", int ageDays = 0)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
            return path;
        }
        internal string Cache(Guid? id = null, long? ticks = null, string floor = "1f") =>
            Write($"MapAlignmentCache/{id ?? Guid.NewGuid():N}/{ticks ?? DateTime.UtcNow.Ticks}-{floor}-7-fingerprint-EdgesOnly/distance-transform.tiff");
        internal (string Old, string Current) Publication()
        {
            var dir = $"MapPublishing/WebsiteOutbox/IDVB-Map-{Guid.NewGuid():N}";
            var old = Write($"{dir}/maps-old.idvm.secure");
            var current = Write($"{dir}/maps-current.idvm.secure");
            Feed(Path.GetDirectoryName(current)!, Path.GetFileName(current));
            return (old, current);
        }
        internal void Feed(string directory, string package) => File.WriteAllText(Path.Combine(directory, "feed.json"),
            JsonSerializer.Serialize(new { payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { packageUri = package }))) }));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
