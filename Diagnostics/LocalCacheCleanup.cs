namespace IDVBuff.Diagnostics;

/// <summary>Plans are read-only snapshots. Execution never discovers extra files to delete.</summary>
internal static partial class LocalCacheCleanup
{
    internal sealed record FileSnapshot(string Path, long Length, DateTime WrittenUtc, DateTime CreatedUtc);
    internal sealed record Entry(string Category, string Path, bool IsDirectory,
        DateTime WrittenUtc, IReadOnlyList<FileSnapshot> Files, bool Obsolete = false)
    {
        internal long Bytes => Files.Sum(file => file.Length);
    }
    internal sealed record Plan(string Root, IReadOnlyList<Entry> Entries)
    {
        internal long Bytes => Entries.Sum(entry => entry.Bytes);
        internal int FileCount => Entries.Sum(entry => entry.Files.Count);
    }
    internal sealed record Result(long Bytes, int DeletedFiles, int SkippedFiles,
        IReadOnlyList<string> Failures, int AlreadyMissingFiles = 0, long AlreadyMissingBytes = 0);

    internal const long CategoryBudget = 512L * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    internal static Plan Scan(string root, bool automatic = false, DateTime? nowUtc = null,
        Func<IEnumerable<string>>? activePaths = null, long categoryBudget = CategoryBudget)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return new(root, []);
        EnsureSafePath(root, root);
        var now = nowUtc ?? DateTime.UtcNow;
        var protectedPaths = ProtectedPaths(activePaths);
        var entries = Discover(root, protectedPaths).Where(entry =>
            !IsProtected(entry.Path, protectedPaths)).ToArray();
        if (!automatic) return new(root, entries);

        var selected = new List<Entry>();
        foreach (var group in entries.GroupBy(entry => entry.Category))
        {
            var expired = group.Where(entry => entry.Obsolete || entry.WrittenUtc < now - Retention).ToArray();
            selected.AddRange(expired);
            var retained = group.Except(expired).OrderBy(entry => entry.WrittenUtc).ToArray();
            var remaining = retained.Sum(entry => entry.Bytes);
            foreach (var entry in retained)
            {
                if (remaining <= categoryBudget) break;
                selected.Add(entry);
                remaining -= entry.Bytes;
            }
        }
        return new(root, selected);
    }

    internal static Result Execute(Plan plan, Func<IEnumerable<string>>? activePaths = null)
    {
        lock (ExecutionGate) return ExecuteCore(plan, activePaths);
    }

    private static Result ExecuteCore(Plan plan, Func<IEnumerable<string>>? activePaths)
    {
        long bytes = 0;
        long missingBytes = 0;
        var missing = 0;
        var deleted = 0;
        var skipped = 0;
        var failures = new List<string>();
        foreach (var entry in plan.Entries)
        {
            var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void MarkMissing(FileSnapshot file)
            {
                if (!accounted.Add(file.Path)) return;
                missing++;
                missingBytes += file.Length;
            }
            var runtimePaths = activePaths?.Invoke().ToArray() ?? [];
            try
            {
                if (IsProtected(entry.Path, AppDataPaths.GetActiveCachePaths().Concat(runtimePaths)))
                {
                    skipped += entry.Files.Count;
                    continue;
                }
                EnsureSafePath(plan.Root, entry.Path);
                // Enumeration is outside the writer gate: large research snapshots
                // must not stall live recognition while collecting metadata.
                var current = entry.IsDirectory
                    ? Directory.Exists(entry.Path) ? ReadFiles(plan.Root, entry.Path) : []
                    : File.Exists(entry.Path) ? [ReadFile(entry.Path)] : [];
                var currentPaths = current.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in entry.Files.Where(file => !currentPaths.Contains(file.Path))) MarkMissing(file);
                var remaining = entry.Files.Where(file => !accounted.Contains(file.Path)).ToArray();
                if (current.Count != remaining.Length || !current.OrderBy(f => f.Path)
                    .SequenceEqual(remaining.OrderBy(f => f.Path)))
                {
                    skipped += remaining.Length;
                    continue;
                }
                foreach (var file in remaining)
                {
                    // Writers may have started since enumeration. Recheck under
                    // the gate, but hold it for at most one file operation.
                    lock (AppDataPaths.CacheIoGate)
                    {
                        try
                        {
                            if (IsProtected(entry.Path, AppDataPaths.GetActiveCachePaths().Concat(runtimePaths))
                                || (entry.Category == "历史发布包" && IsCurrentPublication(plan.Root, entry.Path)))
                            {
                                skipped++;
                                accounted.Add(file.Path);
                                continue;
                            }
                            EnsureSafePath(plan.Root, file.Path);
                            if (!File.Exists(file.Path))
                            {
                                MarkMissing(file);
                                continue;
                            }
                            if (ReadFile(file.Path) != file)
                            {
                                skipped++;
                                accounted.Add(file.Path);
                                continue;
                            }
                            File.Delete(file.Path);
                            bytes += file.Length;
                            deleted++;
                            accounted.Add(file.Path);
                        }
                        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                        {
                            MarkMissing(file);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            accounted.Add(file.Path);
                            failures.Add($"{file.Path}: {error.Message}");
                        }
                    }
                }
                if (entry.IsDirectory) RemoveEmptyDirectories(plan.Root, entry.Path);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A parallel/external cleaner already achieved the requested state.
                foreach (var file in entry.Files.Where(file => !File.Exists(file.Path))) MarkMissing(file);
                skipped += entry.Files.Count(file => !accounted.Contains(file.Path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{entry.Path}: {error.Message}");
            }
        }
        return new(bytes, deleted, skipped, failures, missing, missingBytes);
    }

    private static string[] ProtectedPaths(Func<IEnumerable<string>>? activePaths) =>
        AppDataPaths.GetActiveCachePaths().Concat(activePaths?.Invoke() ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();

    private static bool IsProtected(string path, IEnumerable<string> activePaths) =>
        activePaths.Any(active => ContainsPath(path, active) || ContainsPath(active, path));

    private static bool ContainsPath(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafePath(string root, string path)
    {
        path = Path.GetFullPath(path);
        if (!ContainsPath(root, path)) throw new IOException("清理路径超出数据目录。");
        // Reject junctions in the root's ancestors too, including a replaced root.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"清理路径包含链接，已保留：{current}");
        }
    }

    private static FileSnapshot ReadFile(string path)
    {
        var info = new FileInfo(path);
        return Snapshot(info);
    }

    private static FileSnapshot Snapshot(FileInfo info) =>
        new(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);

    private static List<FileSnapshot> ReadFiles(string root, string directory)
    {
        var files = new List<FileSnapshot>();
        EnsureSafePath(root, directory);
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"清理路径包含链接，已保留：{info.FullName}");
            if (info is DirectoryInfo child) files.AddRange(ReadFiles(root, child.FullName));
            else if (info is FileInfo file) files.Add(Snapshot(file));
        }
        return files;
    }

    private static void RemoveEmptyDirectories(string root, string directory)
    {
        EnsureSafePath(root, directory);
        if (!Directory.Exists(directory)) return;
        foreach (var child in Directory.EnumerateDirectories(directory))
            RemoveEmptyDirectories(root, child);
        lock (AppDataPaths.CacheIoGate)
        {
            if (!IsProtected(directory, AppDataPaths.GetActiveCachePaths())
                && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
