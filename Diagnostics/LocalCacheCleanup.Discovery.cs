using System.Text.Json;
using System.Text.RegularExpressions;

namespace IDVBuff.Diagnostics;

internal static partial class LocalCacheCleanup
{
    private static readonly HashSet<string> StructureFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "nuisance-mask.png", "structure-mask.png", "edges.png", "normalized-gray.png",
        "edges-half.png", "edges-quarter.png", "akaze-descriptors.png", "akaze-keypoints.json",
        "repeated-regions.png", "distance-transform.tiff", "metadata.json"
    };

    private static IEnumerable<Entry> Discover(string root, string[] protectedPaths)
    {
        var catalog = ReadCatalog(root);
        foreach (var map in Directories(root, Path.Combine(root, "MapAlignmentCache")))
        {
            if (IsProtected(map, protectedPaths)) continue;
            if (!Guid.TryParseExact(Path.GetFileName(map), "N", out var id)) continue;
            foreach (var version in Directories(root, map))
            {
                var name = Path.GetFileName(version);
                var tickText = name.Split('-')[0];
                if (!long.TryParse(tickText, out var ticks) || !name.Contains('-')) continue;
                var entry = DirectoryEntry(root, "结构缓存", version);
                if (entry.Files.Any(file => Path.GetDirectoryName(file.Path) != version
                    || (!StructureFiles.Contains(Path.GetFileName(file.Path))
                        && !Regex.IsMatch(Path.GetFileName(file.Path), @"^vpsg-scale-graph-[0-9A-Fa-f]+\.json$"))))
                    continue;
                yield return entry with { Obsolete = catalog is not null
                    && (!catalog.TryGetValue(id, out var currentTicks) || currentTicks != ticks) };
            }
        }

        foreach (var path in Directories(root, Path.Combine(root, "AlignmentResearch", "sessions")))
            if (!IsProtected(path, protectedPaths)) yield return DirectoryEntry(root, "研究数据", path);
        foreach (var path in Directories(root, Path.Combine(root, "AlignmentResearch")))
        {
            if (!IsProtected(path, protectedPaths) && Regex.IsMatch(Path.GetFileName(path), @"^scan-snapshots-\d{8}$"))
                yield return DirectoryEntry(root, "研究快照", path);
        }
        foreach (var path in Directories(root, Path.Combine(root, "诊断模式")))
        {
            if (!IsProtected(path, protectedPaths) && Regex.IsMatch(Path.GetFileName(path), @"^对局 \d+$"))
                yield return DirectoryEntry(root, "诊断图片", path);
        }
        var logs = Path.Combine(root, "Logs");
        if (Directory.Exists(logs))
        {
            EnsureSafePath(root, logs);
            foreach (var file in Directory.EnumerateFiles(logs))
            {
                if (IsProtected(file, protectedPaths)) continue;
                if (!Regex.IsMatch(Path.GetFileName(file),
                    @"^(output-log-.*\.log|startup-.*\.log|updater-.*\.log|scan-log-.*\.json(?:\.tmp)?|startup\.log|updater\.log|flush-errors\.log)$"))
                    continue;
                EnsureSafePath(root, file);
                var snapshot = ReadFile(file);
                yield return new("日志", file, false, snapshot.WrittenUtc, [snapshot]);
            }
        }
        foreach (var directory in Directories(root, Path.Combine(root, "MapPublishing", "WebsiteOutbox")))
        {
            if (IsProtected(directory, protectedPaths)) continue;
            if (!Regex.IsMatch(Path.GetFileName(directory), @"^IDVB-Map-[0-9a-fA-F]{32}$")) continue;
            // A missing/malformed feed may be an interrupted publication. Preserve it.
            var current = CurrentPackage(root, directory);
            if (current is null) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "maps-*.idvm.secure"))
            {
                if (string.Equals(Path.GetFileName(file), current, StringComparison.OrdinalIgnoreCase)) continue;
                EnsureSafePath(root, file);
                var snapshot = ReadFile(file);
                yield return new("历史发布包", file, false, snapshot.WrittenUtc, [snapshot]);
            }
        }
    }

    private static IEnumerable<string> Directories(string root, string path)
    {
        if (!Directory.Exists(path)) return [];
        EnsureSafePath(root, path);
        var directories = Directory.GetDirectories(path);
        foreach (var directory in directories) EnsureSafePath(root, directory);
        return directories;
    }

    private static Entry DirectoryEntry(string root, string category, string path)
    {
        var files = ReadFiles(root, path);
        // Use the most recent write, not creation time: an old session can still be active.
        var written = files.Count == 0 ? Directory.GetLastWriteTimeUtc(path)
            : files.Max(file => file.WrittenUtc);
        return new(category, path, true, written, files);
    }

    private static Dictionary<Guid, long>? ReadCatalog(string root)
    {
        var path = Path.Combine(root, "Maps", "maps.json");
        if (!File.Exists(path)) return null;
        EnsureSafePath(root, path);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var maps = doc.RootElement.GetProperty("Maps");
            var result = new Dictionary<Guid, long>();
            foreach (var map in maps.EnumerateArray())
                result.Add(map.GetProperty("Id").GetGuid(), map.GetProperty("UpdatedAt").GetDateTimeOffset().UtcTicks);
            return result;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException)
        {
            return null; // Never infer "all maps removed" from an unavailable catalog.
        }
    }

    private static bool IsCurrentPublication(string root, string file)
    {
        var current = CurrentPackage(root, Path.GetDirectoryName(file)!);
        return current is null || string.Equals(current, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
    }

    private static string? CurrentPackage(string root, string directory)
    {
        try
        {
            var feed = Path.Combine(directory, "feed.json");
            EnsureSafePath(root, feed);
            using var envelope = JsonDocument.Parse(File.ReadAllText(feed));
            var payload = envelope.RootElement.EnumerateObject().First(p =>
                p.Name.Equals("payload", StringComparison.OrdinalIgnoreCase)).Value.GetString();
            using var decoded = JsonDocument.Parse(Convert.FromBase64String(payload!));
            var package = decoded.RootElement.EnumerateObject().First(p =>
                p.Name.Equals("packageUri", StringComparison.OrdinalIgnoreCase)).Value.GetString();
            return package is not null && Path.GetFileName(package) == package ? package : null;
        }
        catch (Exception error) when (error is IOException or JsonException or FormatException
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
