using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private const string VariantBackupDirectoryName = "variant-groups-v16";
    private const string VariantBackupManifestName = "backup-manifest.json";

    public string VariantMigrationBackupRoot => Path.Combine(
        Directory.GetParent(_rootDirectory)?.FullName ?? _rootDirectory,
        "Backups",
        VariantBackupDirectoryName);

    private async Task EnsureVariantMigrationBackupAsync()
    {
        if (!File.Exists(CatalogPath))
            return;

        var catalogHash = await ComputeFileSha256Async(CatalogPath);
        if (await HasVerifiedVariantBackupAsync(catalogHash))
            return;

        var appDataRoot = Directory.GetParent(_rootDirectory)?.FullName
            ?? throw new InvalidOperationException("无法解析地图数据目录的父路径，已中止变体数据迁移。");
        var runtimeDirectory = Path.Combine(appDataRoot, "MapRuntime");
        var sources = new List<(string Name, string Directory)>
        {
            ("Maps", _rootDirectory)
        };
        if (Directory.Exists(runtimeDirectory))
            sources.Add(("MapRuntime", runtimeDirectory));

        var files = sources
            .SelectMany(source => Directory.EnumerateFiles(
                    source.Directory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => new VariantBackupSourceFile(
                    source.Name,
                    source.Directory,
                    path,
                    new FileInfo(path).Length)))
            .OrderBy(file => file.Area, StringComparer.Ordinal)
            .ThenBy(file => file.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredBytes = files.Sum(file => file.Length);
        var backupRoot = VariantMigrationBackupRoot;
        Directory.CreateDirectory(backupRoot);
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(backupRoot));
        if (!string.IsNullOrWhiteSpace(driveRoot))
        {
            var available = new DriveInfo(driveRoot).AvailableFreeSpace;
            if (available < requiredBytes)
            {
                throw new IOException(
                    $"变体迁移备份空间不足：需要至少 {requiredBytes} 字节，当前可用 {available} 字节。"
                    + "地图目录尚未修改。");
            }
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var pending = Path.Combine(backupRoot, $".pending-{stamp}-{Guid.NewGuid():N}");
        var completed = Path.Combine(backupRoot, stamp);
        Directory.CreateDirectory(pending);
        try
        {
            var manifestFiles = new List<VariantBackupFileEntry>(files.Length);
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(file.SourceRoot, file.SourcePath);
                var logicalPath = $"{file.Area}/{relative.Replace('\\', '/')}";
                var destination = Path.Combine(pending, file.Area, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var sourceHash = await CopyAndHashAsync(file.SourcePath, destination);
                var destinationHash = await ComputeFileSha256Async(destination);
                if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
                    throw new IOException($"变体迁移备份校验失败：{logicalPath}");
                manifestFiles.Add(new VariantBackupFileEntry(
                    logicalPath,
                    file.Length,
                    sourceHash));
            }

            var manifest = new VariantBackupManifest(
                1,
                DateTimeOffset.UtcNow,
                Path.GetFullPath(_rootDirectory),
                catalogHash,
                manifestFiles);
            var manifestPath = Path.Combine(pending, VariantBackupManifestName);
            await File.WriteAllBytesAsync(
                manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions));
            var persistedManifest = JsonSerializer.Deserialize<VariantBackupManifest>(
                await File.ReadAllBytesAsync(manifestPath),
                SerializerOptions)
                ?? throw new InvalidDataException("无法回读变体迁移备份清单。");
            await VerifyVariantBackupAsync(pending, persistedManifest, catalogHash);
            var collision = 1;
            while (Directory.Exists(completed))
                completed = Path.Combine(backupRoot, $"{stamp}-{collision++:D2}");
            Directory.Move(pending, completed);
            System.Diagnostics.Debug.WriteLine(
                $"[MapRepository] variant migration backup created: {completed}");
        }
        catch
        {
            if (Directory.Exists(pending))
                Directory.Delete(pending, recursive: true);
            throw;
        }
    }

    private async Task<bool> HasVerifiedVariantBackupAsync(string catalogHash)
    {
        var root = VariantMigrationBackupRoot;
        if (!Directory.Exists(root))
            return false;
        foreach (var directory in Directory.EnumerateDirectories(root)
            .Where(path => !Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal)))
        {
            var manifestPath = Path.Combine(directory, VariantBackupManifestName);
            if (!File.Exists(manifestPath))
                continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<VariantBackupManifest>(
                    await File.ReadAllBytesAsync(manifestPath),
                    SerializerOptions);
                if (manifest is null
                    || !string.Equals(manifest.SourceCatalogSha256, catalogHash, StringComparison.Ordinal))
                    continue;
                await VerifyVariantBackupAsync(directory, manifest, catalogHash);
                return true;
            }
            catch
            {
                // A damaged backup is never accepted as migration proof.
            }
        }
        return false;
    }

    private static async Task VerifyVariantBackupAsync(
        string backupDirectory,
        VariantBackupManifest manifest,
        string catalogHash)
    {
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.SourceCatalogSha256, catalogHash, StringComparison.Ordinal)
            || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("变体迁移备份清单无效。");
        }
        foreach (var file in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(
                backupDirectory,
                file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(
                    Path.GetFullPath(backupDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)
                || new FileInfo(path).Length != file.Length
                || !string.Equals(
                    await ComputeFileSha256Async(path),
                    file.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"变体迁移备份文件校验失败：{file.Path}");
            }
        }
    }

    private static async Task<string> CopyAndHashAsync(string source, string destination)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer);
            if (count == 0)
                break;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count));
        }
        await output.FlushAsync();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed record VariantBackupSourceFile(
        string Area,
        string SourceRoot,
        string SourcePath,
        long Length);

    private sealed record VariantBackupFileEntry(
        string Path,
        long Length,
        string Sha256);

    private sealed record VariantBackupManifest(
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        string SourceMapsDirectory,
        string SourceCatalogSha256,
        IReadOnlyList<VariantBackupFileEntry> Files);
}
