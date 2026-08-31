using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapLearningExportValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static MapLearningExportValidationResult Validate(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entries = archive.Entries.ToDictionary(
                entry => NormalizeEntryName(entry.FullName),
                StringComparer.Ordinal);
            if (!entries.TryGetValue("manifest.json", out var manifestEntry))
                return Invalid("训练包缺少 manifest.json。");

            var manifest = ReadJson<ExportManifest>(manifestEntry);
            if (manifest is null || manifest.SchemaVersion != 1)
                return Invalid("训练包清单版本无效。");
            if (!string.Equals(
                    manifest.PreprocessingVersion,
                    MapLearningPreprocessor.Version,
                    StringComparison.Ordinal))
            {
                return Invalid("训练包预处理版本与当前客户端不匹配。");
            }

            foreach (var (entryName, expectedHash) in manifest.Files)
            {
                if (!IsSafeEntryName(entryName)
                    || !entries.TryGetValue(entryName, out var entry))
                {
                    return Invalid($"训练包文件缺失或路径无效：{entryName}");
                }
                using var stream = entry.Open();
                var actualHash = Convert.ToHexString(SHA256.HashData(stream))
                    .ToLowerInvariant();
                if (!string.Equals(actualHash, expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid($"训练包文件哈希不匹配：{entryName}");
                }
            }

            var sampleEntries = entries
                .Where(pair => pair.Key.StartsWith("samples/", StringComparison.Ordinal)
                    && pair.Key.EndsWith("/manifest.json", StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .ToArray();
            if (sampleEntries.Length != manifest.SampleCount)
                return Invalid("训练包样本数量与清单不一致。");

            foreach (var sampleEntry in sampleEntries)
            {
                var sample = ReadJson<MapLearningSampleManifest>(sampleEntry);
                if (sample is null
                    || sample.SchemaVersion < 2
                    || string.IsNullOrWhiteSpace(sample.SampleId)
                    || sample.Candidates.Count == 0
                    || sample.Candidates.Count(candidate => candidate.IsPositive) != 1)
                {
                    return Invalid($"训练样本清单无效：{sampleEntry.FullName}");
                }
                var sampleRoot = $"samples/{sample.SampleId}/";
                var liveName = sampleRoot + sample.LiveImageFile;
                if (!IsSafeFileName(sample.LiveImageFile)
                    || !entries.TryGetValue(liveName, out var liveEntry)
                    || !IsPrivacyScopedImage(liveEntry))
                {
                    return Invalid($"训练样本图像缺失、不可解码或未按隐私范围裁剪：{liveName}");
                }
                foreach (var candidate in sample.Candidates)
                {
                    if (!string.Equals(candidate.ReferenceScope, "floor",
                            StringComparison.Ordinal)
                        || candidate.ReferenceWidth
                            != MapLearningPreprocessor.ObservationSize
                        || candidate.ReferenceHeight
                            != MapLearningPreprocessor.ObservationSize
                        || candidate.HasTrustedSpatialLabel
                            && (candidate.SpatialCenterX is < 0d or > 1d
                                || candidate.SpatialCenterY is < 0d or > 1d))
                    {
                        return Invalid(
                            $"候选缺少完整楼层或空间标签无效：{sampleEntry.FullName}");
                    }
                    var referenceName = "references/" + candidate.ReferenceFile;
                    if (!IsSafeFileName(candidate.ReferenceFile)
                        || !entries.TryGetValue(referenceName, out var referenceEntry)
                        || !IsPrivacyScopedImage(referenceEntry))
                    {
                        return Invalid($"候选参考图缺失、不可解码或未按隐私范围裁剪：{referenceName}");
                    }
                    if (!string.Equals(
                            ComputeSha256(referenceEntry),
                            candidate.ReferenceHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Invalid($"候选参考图与样本标签哈希不一致：{referenceName}");
                    }
                }
            }

            var datasetFingerprint = ComputeDatasetFingerprint(manifest.Files);
            return new MapLearningExportValidationResult(
                true,
                manifest.SampleCount,
                "训练包结构、哈希、样本引用和隐私范围图像均有效。",
                datasetFingerprint);
        }
        catch (Exception exception)
        {
            return Invalid($"训练包无法读取：{exception.Message}");
        }
    }

    private static bool IsPrivacyScopedImage(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        using var image = Cv2.ImDecode(memory.ToArray(), ImreadModes.Unchanged);
        if (image.Empty()
            || image.Width != MapLearningPreprocessor.ObservationSize
            || image.Height != MapLearningPreprocessor.ObservationSize
            || image.Channels() is < 1 or > 4)
        {
            return false;
        }
        var trainingInputs = MapLearningPreprocessor.CreateTrainingInputs(image);
        return trainingInputs.Count > 0
            && trainingInputs.All(input =>
                input.Length == MapLearningPreprocessor.ChannelCount
                    * MapLearningPreprocessor.InputSize
                    * MapLearningPreprocessor.InputSize);
    }

    private static string ComputeSha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeDatasetFingerprint(
        IReadOnlyDictionary<string, string> files)
    {
        var canonical = string.Join('\n', files
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}\0{pair.Value.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static T? ReadJson<T>(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static string NormalizeEntryName(string value) =>
        value.Replace('\\', '/');

    private static bool IsSafeEntryName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.Contains("../", StringComparison.Ordinal)
        && !value.Contains(':');

    private static bool IsSafeFileName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal);

    private static MapLearningExportValidationResult Invalid(string reason) =>
        new(false, 0, reason);

    private sealed class ExportManifest
    {
        public int SchemaVersion { get; set; }
        public string PreprocessingVersion { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public Dictionary<string, string> Files { get; set; } =
            new(StringComparer.Ordinal);
    }
}

internal sealed record MapLearningExportValidationResult(
    bool IsValid,
    int SampleCount,
    string Message,
    string DatasetFingerprint = "");
