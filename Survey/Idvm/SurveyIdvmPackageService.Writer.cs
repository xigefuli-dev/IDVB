using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Idvm;

public sealed partial class SurveyIdvmPackageService
{
    private async Task ExportCoreAsync(
        Guid projectId,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("IDVM destination must be writable.", nameof(destination));
        var project = await _projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("要导出的测绘项目不存在。");
        var assets = EnumerateAssets(project)
            .GroupBy(item => item.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Sha256, StringComparer.Ordinal)
            .ToArray();
        var packageId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var manifest = new SurveyIdvmManifest
        {
            PackageId = packageId,
            CreatedAt = createdAt,
            Project = project,
            Capabilities =
            [
                "survey.layers",
                "survey.transforms",
                "survey.constraints",
                "survey.dual-output",
                "survey.display-state",
                "survey.layer-masks",
                "survey.provenance"
            ],
            Files = assets.Select(asset => new SurveyIdvmFile
            {
                Path = AssetPath(asset),
                Sha256 = asset.Sha256,
                Size = asset.ByteLength,
                MediaType = asset.MediaType,
                PixelWidth = asset.PixelWidth,
                PixelHeight = asset.PixelHeight
            }).ToList()
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        await WriteBytesAsync(
            archive.CreateEntry("header", CompressionLevel.NoCompression),
            SurveyIdvmSecurity.CreateHeader(packageId, createdAt, SHA256.HashData(manifestBytes)),
            cancellationToken);
        await WriteBytesAsync(
            archive.CreateEntry("manifest.json", CompressionLevel.Optimal),
            manifestBytes,
            cancellationToken);
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(AssetPath(asset), CompressionLevel.Optimal);
            await using var input = await _assets.OpenReadAsync(projectId, asset, cancellationToken)
                .ConfigureAwait(false);
            await using var output = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied = checked(copied + read);
            }
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (copied != asset.ByteLength
                || !string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"测绘资产在导出时校验失败：{asset.Sha256}");
        }
    }

    private static IEnumerable<SurveyAssetReference> EnumerateAssets(SurveyProjectSnapshot project)
    {
        foreach (var observation in project.Observations)
        {
            yield return observation.SourceAsset;
            if (observation.StructureAsset is { } structure)
                yield return structure;
            if (observation.FeatureAsset is { } feature)
                yield return feature;
            if (observation.DisplayAsset is { } display)
                yield return display;
            if (observation.VisibleMaskAsset is { } visibleMask)
                yield return visibleMask;
        }
        foreach (var layer in project.Layers)
        {
            if (layer.HiddenMaskAsset is { } hiddenMask)
                yield return hiddenMask;
            if (layer.ColorFilterAsset is { } colorFilter)
                yield return colorFilter;
        }
    }

    private static string AssetPath(SurveyAssetReference asset)
    {
        var extension = Path.GetExtension(asset.RelativePath);
        if (extension.Length is < 2 or > 10)
            extension = ".bin";
        return $"assets/{asset.Sha256[..2]}/{asset.Sha256}{extension.ToLowerInvariant()}";
    }

    private static async Task WriteBytesAsync(
        ZipArchiveEntry entry,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
