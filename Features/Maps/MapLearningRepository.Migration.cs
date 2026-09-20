using System.Security.Cryptography;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record LegacySampleMigrationResult(
    int MigratedMatchCount,
    int SkippedMatchCount);

internal sealed partial class MapLearningRepository
{
    public async Task<LegacySampleMigrationResult> MigrateLegacySamplesAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureCreated();
            var appDataRoot = Directory.GetParent(RootDirectory)?.FullName;
            var mapsDirectory = appDataRoot is null
                ? string.Empty : Path.Combine(appDataRoot, "Maps");
            var catalogPath = Path.Combine(mapsDirectory, "maps.json");
            var catalog = await ReadJsonAsync<LearningMapCatalog>(catalogPath,
                cancellationToken);
            if (catalog is null)
                return new LegacySampleMigrationResult(0, 0);

            var latest = await LoadLatestSampleFilesCoreAsync(cancellationToken);
            var migrated = 0;
            var skipped = 0;
            foreach (var item in latest.Where(item => item.Manifest.SchemaVersion < 2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = await BuildMigratedCandidatesAsync(
                    item.Manifest.Candidates, catalog, mapsDirectory,
                    cancellationToken);
                if (candidates is null)
                {
                    skipped++;
                    continue;
                }

                var backupPath = Path.Combine(item.Directory,
                    "manifest.schema-v1.json");
                if (!File.Exists(backupPath))
                    File.Copy(item.ManifestPath, backupPath);
                var upgraded = item.Manifest with
                {
                    SchemaVersion = 2,
                    MigratedFromSchemaVersion = item.Manifest.SchemaVersion,
                    Candidates = candidates
                };
                await WriteJsonAtomicAsync(item.ManifestPath, upgraded,
                    cancellationToken);
                migrated++;
            }
            return new LegacySampleMigrationResult(migrated, skipped);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<MapLearningCandidateManifest>?>
        BuildMigratedCandidatesAsync(
            IReadOnlyList<MapLearningCandidateManifest> candidates,
            LearningMapCatalog catalog,
            string mapsDirectory,
            CancellationToken cancellationToken)
    {
        var result = new List<MapLearningCandidateManifest>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var map = catalog.Maps.FirstOrDefault(item => item.Id == candidate.MapId);
            var floorKey = MapScanFloorRules.NormalizeFloorIdentity(
                candidate.FloorKey) ?? candidate.FloorKey.Trim();
            var floor = map?.Floors.FirstOrDefault(item => string.Equals(
                item.Key, floorKey, StringComparison.OrdinalIgnoreCase));
            var fileName = string.IsNullOrWhiteSpace(floor?.OverlayFileName)
                ? floor?.ImageFileName : floor.OverlayFileName;
            if (map is null || floor is null
                || string.IsNullOrWhiteSpace(fileName))
                return null;
            var mapDirectory = Path.Combine(mapsDirectory,
                map.Id.ToString("N"));
            var imagePath = Path.GetFullPath(Path.Combine(mapDirectory,
                fileName));
            if (!imagePath.StartsWith(mapDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(imagePath))
                return null;
            using var reference = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
            if (reference.Empty())
                return null;
            var bytes = MapLearningPreprocessor.EncodeReferenceFloorPng(reference);
            var hash = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            var referenceName = hash + ".png";
            var referencePath = Path.Combine(ReferencesDirectory, referenceName);
            if (!File.Exists(referencePath))
                await WriteAtomicBytesAsync(referencePath, bytes,
                    cancellationToken);
            result.Add(candidate with
            {
                FloorKey = floorKey,
                ReferenceHash = hash,
                ReferenceFile = referenceName,
                ReferenceScope = "floor",
                ReferenceWidth = MapLearningPreprocessor.ObservationSize,
                ReferenceHeight = MapLearningPreprocessor.ObservationSize,
                HasTrustedSpatialLabel = false,
                SpatialCenterX = 0d,
                SpatialCenterY = 0d
            });
        }
        return result;
    }

    private async Task<IReadOnlyList<LegacySampleFile>>
        LoadLatestSampleFilesCoreAsync(CancellationToken cancellationToken)
    {
        var items = new List<LegacySampleFile>();
        foreach (var directory in Directory.EnumerateDirectories(SamplesDirectory)
            .Where(path => !path.EndsWith(".tmp",
                StringComparison.OrdinalIgnoreCase)))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var manifest = await ReadJsonAsync<MapLearningSampleManifest>(
                manifestPath, cancellationToken);
            if (manifest is not null)
                items.Add(new LegacySampleFile(directory, manifestPath, manifest));
        }
        return items.GroupBy(item => item.Manifest.MatchId)
            .Select(group => group.OrderByDescending(item =>
                    item.Manifest.CreatedAt)
                .ThenByDescending(item => item.Manifest.SampleId,
                    StringComparer.Ordinal)
                .First())
            .ToArray();
    }

    private sealed record LegacySampleFile(
        string Directory,
        string ManifestPath,
        MapLearningSampleManifest Manifest);

    private sealed record LearningMapCatalog
    {
        public IReadOnlyList<LearningMapEntry> Maps { get; init; } = [];
    }

    private sealed record LearningMapEntry
    {
        public Guid Id { get; init; }
        public IReadOnlyList<LearningFloorEntry> Floors { get; init; } = [];
    }

    private sealed record LearningFloorEntry
    {
        public string Key { get; init; } = string.Empty;
        public string ImageFileName { get; init; } = string.Empty;
        public string OverlayFileName { get; init; } = string.Empty;
    }
}
