using System.IO.Compression;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapLearningRepository
{
    public async Task ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
        var samples = MapCandidateLearningEngine.ApplyLatestMatchCorrections(
            await LoadSamplesAsync(cancellationToken))
            .Where(MapCandidateLearningEngine.IsSpatialSampleForExport)
            .ToArray();
        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var sample in samples)
            {
                var sourceDirectory = Path.Combine(SamplesDirectory, sample.SampleId);
                AddBytes(archive,
                    JsonSerializer.SerializeToUtf8Bytes(sample, JsonOptions),
                    $"samples/{sample.SampleId}/manifest.json",
                    hashes);
                AddPrivacyScopedImage(
                    archive,
                    Path.Combine(sourceDirectory, sample.LiveImageFile),
                    $"samples/{sample.SampleId}/{sample.LiveImageFile}",
                    hashes);
            }
            foreach (var reference in samples.SelectMany(item => item.Candidates)
                .Select(item => item.ReferenceFile).Distinct(StringComparer.Ordinal))
            {
                AddFile(archive, Path.Combine(ReferencesDirectory, reference),
                    $"references/{reference}", hashes);
            }
            var exportManifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                preprocessingVersion = MapLearningPreprocessor.Version,
                createdAt = DateTimeOffset.UtcNow,
                sampleCount = samples.Length,
                files = hashes
            }, JsonOptions);
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(exportManifest);
        }
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static void AddPrivacyScopedImage(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        IDictionary<string, string> hashes)
    {
        if (!File.Exists(sourcePath))
            return;

        using var source = OpenCvSharp.Cv2.ImRead(
            sourcePath,
            OpenCvSharp.ImreadModes.Unchanged);
        if (source.Empty())
            throw new InvalidDataException(
                $"训练样本图像无法解码：{Path.GetFileName(sourcePath)}");

        AddBytes(
            archive,
            MapLearningPreprocessor.EncodePrivacyScopedPng(source),
            entryName,
            hashes);
    }
}
