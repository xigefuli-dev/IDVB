using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapLearningRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _maximumSampleBytes;

    public MapLearningRepository(
        string? rootDirectory = null,
        long maximumSampleBytes = 2L * 1024L * 1024L * 1024L)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "MapLearning"));
        _maximumSampleBytes = Math.Max(64L * 1024L * 1024L,
            maximumSampleBytes);
    }

    public string RootDirectory { get; }
    public string SamplesDirectory => Path.Combine(RootDirectory, "samples");
    public string ReferencesDirectory => Path.Combine(RootDirectory, "references");
    public string ModelsDirectory => Path.Combine(RootDirectory, "models");
    public string CurrentReferencePath => Path.Combine(RootDirectory, "CURRENT");
    public string BestExperimentalReferencePath =>
        Path.Combine(RootDirectory, "BEST_EXPERIMENTAL");
    public string LastKnownGoodReferencePath =>
        Path.Combine(RootDirectory, "LAST_KNOWN_GOOD");
    private string StatePath => Path.Combine(RootDirectory, "state.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(SamplesDirectory);
        Directory.CreateDirectory(ReferencesDirectory);
        Directory.CreateDirectory(ModelsDirectory);
    }

    public async Task<MapLearningSampleManifest> SaveHumanSelectionAsync(
        Guid matchId,
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        Guid selectedMapId,
        CancellationToken cancellationToken,
        MapScreenRect? viewportBounds = null)
    {
        var selected = choices.FirstOrDefault(choice =>
            choice.Recognition.Map.Id == selectedMapId)
            ?? throw new ArgumentException("人工选择不在候选集合中。",
                nameof(selectedMapId));
        var sampleId = $"s-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-"
            + Guid.NewGuid().ToString("N")[..8];
        var temporaryDirectory = Path.Combine(SamplesDirectory, sampleId + ".tmp");
        var finalDirectory = Path.Combine(SamplesDirectory, sampleId);
        var candidates = new List<MapLearningCandidateManifest>(choices.Count);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureCreated();
            Directory.CreateDirectory(temporaryDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(temporaryDirectory, "live.png"),
                MapLearningPreprocessor.EncodePrivacyScopedPng(liveViewport),
                cancellationToken);

            foreach (var choice in choices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var reference = MapLearningPreprocessor
                    .LoadReferenceRegion(choice);
                var bytes = MapLearningPreprocessor
                    .EncodeReferenceFloorPng(reference);
                var hash = Convert.ToHexString(SHA256.HashData(bytes))
                    .ToLowerInvariant();
                var referenceName = hash + ".png";
                var referencePath = Path.Combine(
                    ReferencesDirectory, referenceName);
                if (!File.Exists(referencePath))
                    await WriteAtomicBytesAsync(referencePath, bytes,
                        cancellationToken);
                candidates.Add(new MapLearningCandidateManifest
                {
                    MapId = choice.Recognition.Map.Id,
                    FloorKey = MapScanFloorRules.NormalizeFloorIdentity(
                        choice.Recognition.Result.Floor) ?? string.Empty,
                    ReferenceHash = hash,
                    ReferenceFile = referenceName,
                    ReferenceScope = "floor",
                    ReferenceWidth = MapLearningPreprocessor.ObservationSize,
                    ReferenceHeight = MapLearningPreprocessor.ObservationSize,
                    HasTrustedSpatialLabel = TryResolveSpatialLabel(
                        choice, reference.Size(), viewportBounds,
                        out var spatialX, out var spatialY),
                    SpatialCenterX = spatialX,
                    SpatialCenterY = spatialY,
                    TraditionalScore = choice.TraditionalScore
                        ?? choice.RawConfidence,
                    ModelProbability = choice.ModelProbability,
                    FusionScore = choice.FusionScore,
                    IsPositive = choice.Recognition.Map.Id == selectedMapId
                });
            }

            var manifest = new MapLearningSampleManifest
            {
                SampleId = sampleId,
                MatchId = matchId,
                CreatedAt = DateTimeOffset.UtcNow,
                MapClass = selected.Recognition.Map.Class.Trim(),
                SelectedMapId = selectedMapId,
                Split = ResolveSplit(matchId),
                Candidates = candidates
            };
            await WriteJsonAsync(
                Path.Combine(temporaryDirectory, "manifest.json"),
                manifest,
                cancellationToken);
            Directory.Move(temporaryDirectory, finalDirectory);
            await EnforceSampleBudgetCoreAsync(cancellationToken);
            return manifest;
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MapLearningSampleManifest>> LoadSamplesAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureCreated();
            var samples = new List<MapLearningSampleManifest>();
            foreach (var directory in Directory.EnumerateDirectories(SamplesDirectory)
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var manifest = await ReadJsonAsync<MapLearningSampleManifest>(
                    Path.Combine(directory, "manifest.json"), cancellationToken);
                if (manifest is not null)
                    samples.Add(manifest);
            }
            return samples;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ReadCurrentVersion() => ReadReference(CurrentReferencePath);
    public string? ReadBestExperimentalVersion() =>
        ReadReference(BestExperimentalReferencePath);
    public string? ReadLastKnownGoodVersion() =>
        ReadReference(LastKnownGoodReferencePath);
    public string GetModelDirectory(string version) =>
        Path.Combine(ModelsDirectory, version);

    public async Task<MapModelManifest?> LoadModelManifestAsync(
        string version,
        CancellationToken cancellationToken)
    {
        if (!IsSafeVersion(version))
            return null;
        return await ReadJsonAsync<MapModelManifest>(Path.Combine(
            GetModelDirectory(version), "manifest.json"), cancellationToken);
    }

    public async Task UpdateModelManifestAsync(
        MapModelManifest manifest,
        CancellationToken cancellationToken) =>
        await WriteJsonAtomicAsync(Path.Combine(GetModelDirectory(manifest.Version),
            "manifest.json"), manifest, cancellationToken);

    public async Task<MapLearningStateDocument> LoadStateAsync(
        CancellationToken cancellationToken) =>
        await ReadJsonAsync<MapLearningStateDocument>(StatePath, cancellationToken)
        ?? new MapLearningStateDocument();

    public Task SaveStateAsync(
        MapLearningStateDocument state,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(StatePath, state, cancellationToken);

    public async Task<IReadOnlyList<MapModelManifest>> LoadModelManifestsAsync(
        CancellationToken cancellationToken)
    {
        EnsureCreated();
        var result = new List<MapModelManifest>();
        foreach (var directory in Directory.EnumerateDirectories(ModelsDirectory)
            .Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            var manifest = await ReadJsonAsync<MapModelManifest>(
                Path.Combine(directory, "manifest.json"), cancellationToken);
            if (manifest is not null)
                result.Add(manifest);
        }
        return result.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    private async Task EnforceSampleBudgetCoreAsync(CancellationToken cancellationToken)
    {
        var directories = Directory.EnumerateDirectories(SamplesDirectory)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(path => new DirectoryInfo(path))
            .OrderBy(item => item.CreationTimeUtc)
            .ToList();
        long total = directories.Sum(GetDirectoryLength);
        foreach (var directory in directories)
        {
            if (total <= _maximumSampleBytes)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadJsonAsync<MapLearningSampleManifest>(
                Path.Combine(directory.FullName, "manifest.json"), cancellationToken);
            if (string.Equals(manifest?.Split, "validation",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var length = GetDirectoryLength(directory);
            TryDeleteDirectory(directory.FullName);
            total -= length;
        }
    }

    private static string ResolveSplit(Guid matchId)
    {
        var hash = SHA256.HashData(matchId.ToByteArray());
        return hash[0] % 5 == 0 ? "validation" : "train";
    }

    private static string? ReadReference(string path)
    {
        try
        {
            var value = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return value is not null && IsSafeVersion(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeVersion(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains(Path.DirectorySeparatorChar)
        && !value.Contains(Path.AltDirectorySeparatorChar);

    private static Task WriteReferenceAsync(
        string path, string version, CancellationToken cancellationToken) =>
        WriteAtomicBytesAsync(path, Encoding.UTF8.GetBytes(version + Environment.NewLine),
            cancellationToken);

    private static async Task WriteJsonAsync<T>(
        string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions,
            cancellationToken);
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteAtomicBytesAsync(path, bytes, cancellationToken);
    }

    private static async Task WriteAtomicBytesAsync(
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void AddFile(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        IDictionary<string, string> hashes)
    {
        if (!File.Exists(sourcePath))
            return;
        var bytes = File.ReadAllBytes(sourcePath);
        AddBytes(archive, bytes, entryName, hashes);
    }

    private static void AddBytes(
        ZipArchive archive,
        byte[] bytes,
        string entryName,
        IDictionary<string, string> hashes)
    {
        hashes[entryName] = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static long GetDirectoryLength(DirectoryInfo directory) =>
        directory.Exists
            ? directory.EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length)
            : 0L;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
