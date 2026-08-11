using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Idvm;

public sealed partial class SurveyIdvmPackageService
{
    private async Task<SurveyProjectSnapshot> ImportCoreAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("IDVM source must be readable.", nameof(source));
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 3 or > SurveyIdvmSecurity.MaximumEntries)
            throw new InvalidDataException("IDVM 1.1 包的条目数量无效。");
        var entries = ValidateEntries(archive);
        var manifestBytes = await ReadLimitedAsync(
            entries.GetValueOrDefault("manifest.json")
                ?? throw new InvalidDataException("IDVM 包缺少 manifest.json。"),
            SurveyIdvmSecurity.MaximumManifestBytes,
            cancellationToken);
        ValidateJsonShape(manifestBytes);
        var manifest = JsonSerializer.Deserialize<SurveyIdvmManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("IDVM manifest 不能为空。");
        ValidateManifest(manifest, entries);
        var headerBytes = await ReadLimitedAsync(
            entries.GetValueOrDefault("header")
                ?? throw new InvalidDataException("IDVM 包缺少 header。"),
            SurveyIdvmSecurity.HeaderSize,
            cancellationToken);
        SurveyIdvmSecurity.ValidateHeader(
            headerBytes,
            manifest.PackageId,
            manifest.CreatedAt,
            SHA256.HashData(manifestBytes));

        var importedProjectId = Guid.NewGuid();
        var importedAssets = new Dictionary<string, SurveyAssetReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[file.Path];
            await using var entryStream = entry.Open();
            var imported = await _assets.PutStreamAsync(
                importedProjectId,
                entryStream,
                Path.GetExtension(file.Path),
                file.MediaType,
                file.PixelWidth,
                file.PixelHeight,
                file.Sha256,
                cancellationToken).ConfigureAwait(false);
            if (imported.ByteLength != file.Size)
                throw new InvalidDataException($"IDVM 资产展开大小不匹配：{file.Path}");
            importedAssets.Add(file.Sha256, imported);
        }

        var remapped = RemapSnapshot(
            manifest.Project!,
            importedProjectId,
            importedAssets,
            legacy11: manifest.FormatVersion == "1.1");
        return await _projects.ImportSnapshotAsync(remapped, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;
            SurveyIdvmSecurity.ValidatePath(entry.FullName);
            if (!result.TryAdd(entry.FullName, entry))
                throw new InvalidDataException($"IDVM 包含重复条目：{entry.FullName}");
            if (entry.Length < 0 || entry.Length > SurveyIdvmSecurity.MaximumSingleAssetBytes)
                throw new InvalidDataException($"IDVM 条目超过单资产限制：{entry.FullName}");
            total = checked(total + entry.Length);
            if (total > SurveyIdvmSecurity.MaximumExpandedBytes)
                throw new InvalidDataException("IDVM 1.1 包展开后超过 4 GiB 限制。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000))
                throw new InvalidDataException($"IDVM 条目不是普通文件：{entry.FullName}");
        }
        return result;
    }

    private static void ValidateManifest(
        SurveyIdvmManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (manifest.Format != "idvm"
            || manifest.FormatVersion is not ("1.1" or "1.2")
            || manifest.MinimumReaderVersion != manifest.FormatVersion
            || manifest.PackageType != "survey-project")
            throw new NotSupportedException("该数据包不是受支持的 IDVM 1.1 survey-project。");
        if (manifest.PackageId == Guid.Empty || manifest.Project is null)
            throw new InvalidDataException("IDVM 测绘清单缺少项目或包标识。");
        var requiredCapabilities = new[]
        {
            "survey.layers",
            "survey.transforms",
            "survey.constraints",
            "survey.dual-output",
            "survey.provenance"
        };
        if (requiredCapabilities.Except(manifest.Capabilities, StringComparer.Ordinal).Any())
            throw new NotSupportedException("IDVM 测绘包缺少当前读取器要求的能力声明。");
        if (manifest.FormatVersion == "1.2"
            && new[] { "survey.display-state", "survey.layer-masks" }
                .Except(manifest.Capabilities, StringComparer.Ordinal).Any())
            throw new NotSupportedException("IDVM 1.2 测绘包缺少图层编辑能力声明。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            SurveyIdvmSecurity.ValidatePath(file.Path);
            if (!file.Path.StartsWith("assets/", StringComparison.Ordinal)
                || !paths.Add(file.Path)
                || !hashes.Add(file.Sha256)
                || !SurveyIdvmSecurity.IsSha256(file.Sha256)
                || file.Size is <= 0 or > SurveyIdvmSecurity.MaximumSingleAssetBytes
                || file.PixelWidth <= 0
                || file.PixelHeight <= 0
                || !entries.TryGetValue(file.Path, out var entry)
                || entry.Length != file.Size)
                throw new InvalidDataException($"IDVM 测绘资产声明无效：{file.Path}");
        }
        var expectedPaths = paths.Append("header").Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        if (!expectedPaths.SetEquals(entries.Keys))
            throw new InvalidDataException("IDVM manifest 文件清单与 ZIP 条目不一致。");
        var referencedHashes = EnumerateAssets(manifest.Project)
            .Select(item => item.Sha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!referencedHashes.SetEquals(hashes))
            throw new InvalidDataException("IDVM 项目资产引用与文件清单不一致。");
    }

    private static SurveyProjectSnapshot RemapSnapshot(
        SurveyProjectSnapshot source,
        Guid projectId,
        IReadOnlyDictionary<string, SurveyAssetReference> assets,
        bool legacy11)
    {
        SurveyAssetReference? MapOptional(SurveyAssetReference? asset) =>
            asset is null ? null : assets[asset.Sha256];
        var project = source.Project with
        {
            ProjectId = projectId,
            State = source.Project.State is SurveyProjectState.Published or SurveyProjectState.Archived
                ? SurveyProjectState.NeedsReview
                : source.Project.State,
            PublishedRevision = null
        };
        var observations = source.Observations.Select(item => item with
        {
            ProjectId = projectId,
            SourceAsset = assets[item.SourceAsset.Sha256],
            StructureAsset = MapOptional(item.StructureAsset),
            FeatureAsset = MapOptional(item.FeatureAsset),
            DisplayAsset = MapOptional(item.DisplayAsset),
            VisibleMaskAsset = MapOptional(item.VisibleMaskAsset)
        }).ToArray();
        var observationsById = observations.ToDictionary(item => item.ObservationId);
        var layers = source.Layers.Select(item => item with
        {
            ProjectId = projectId,
            UsesCleanedDisplay = legacy11
                ? observationsById[item.ObservationId].DisplayAsset is not null
                : item.UsesCleanedDisplay,
            HiddenMaskAsset = MapOptional(item.HiddenMaskAsset)
        }).ToArray();
        var constraints = source.Constraints.Select(item => item with { ProjectId = projectId }).ToArray();
        return new SurveyProjectSnapshot(project, source.Floors, observations, layers, constraints);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        ZipArchiveEntry entry,
        long limit,
        CancellationToken cancellationToken)
    {
        if (entry.Length > limit)
            throw new InvalidDataException($"IDVM 条目超过读取限制：{entry.FullName}");
        await using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (output.Length != entry.Length || output.Length > limit)
            throw new InvalidDataException($"IDVM 条目实际大小与声明不一致：{entry.FullName}");
        return output.ToArray();
    }

    private static void ValidateJsonShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 96 });
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"IDVM manifest 包含重复字段：{property.Name}");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }
}
