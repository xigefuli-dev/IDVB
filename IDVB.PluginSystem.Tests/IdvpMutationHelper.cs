using System.IO.Compression;

namespace IDVB.PluginSystem.Tests;

internal static class IdvpMutationHelper
{
    public static async Task RewriteAsync(
        string inputPath,
        string outputPath,
        Func<string, byte[], (bool Include, string Path, byte[] Data)> mutate,
        IReadOnlyList<(string Path, byte[] Data)>? additionalEntries = null)
    {
        var packageBytes = await File.ReadAllBytesAsync(inputPath);
        var header = packageBytes[..80];
        using var inputBytes = new MemoryStream(packageBytes, 80, packageBytes.Length - 80, writable: false);
        using var inputArchive = new ZipArchive(inputBytes, ZipArchiveMode.Read);
        using var zipBytes = new MemoryStream();
        using (var outputArchive = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in inputArchive.Entries)
            {
                await using var entryStream = entry.Open();
                using var dataStream = new MemoryStream();
                await entryStream.CopyToAsync(dataStream);
                var result = mutate(entry.FullName, dataStream.ToArray());
                if (!result.Include) continue;
                var outputEntry = outputArchive.CreateEntry(result.Path, CompressionLevel.Optimal);
                await using var output = outputEntry.Open();
                await output.WriteAsync(result.Data);
            }

            foreach (var (path, data) in additionalEntries ?? [])
            {
                var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
                await using var output = entry.Open();
                await output.WriteAsync(data);
            }
        }

        await using var package = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        await package.WriteAsync(header);
        zipBytes.Position = 0;
        await zipBytes.CopyToAsync(package);
    }
}
