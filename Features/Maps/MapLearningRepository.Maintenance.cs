using System.Security.Cryptography;
using System.Text;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapLearningRepository
{
    private string CreateNextModelVersion(string identityHash)
    {
        var now = DateTimeOffset.Now;
        var prefix = $"m01.0-{now:yy.MM.dd}.";
        var highest = 0;
        foreach (var path in Directory.EnumerateDirectories(ModelsDirectory))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || name.Length < prefix.Length + 4
                || !int.TryParse(name.AsSpan(prefix.Length, 4), out var number))
            {
                continue;
            }
            highest = Math.Max(highest, number);
        }
        return $"{prefix}{highest + 1:D4}-{identityHash[..8]}";
    }

    public async Task ClearSamplesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureCreated();
            var retainedReferences = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(
                SamplesDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = await ReadJsonAsync<MapLearningSampleManifest>(
                    Path.Combine(directory, "manifest.json"), cancellationToken);
                if (string.Equals(manifest?.Split, "validation",
                        StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var candidate in manifest!.Candidates)
                        retainedReferences.Add(candidate.ReferenceFile);
                    continue;
                }
                TryDeleteDirectory(directory);
            }
            foreach (var reference in Directory.EnumerateFiles(
                ReferencesDirectory, "*.png"))
            {
                if (!retainedReferences.Contains(Path.GetFileName(reference)))
                    File.Delete(reference);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<string> ComputeDatasetHashAsync(
        IReadOnlyList<MapLearningSampleManifest> samples,
        CancellationToken cancellationToken)
    {
        var lines = samples.OrderBy(item => item.SampleId).Select(item =>
            $"{item.SampleId}|{item.MatchId:D}|{item.SelectedMapId:D}|"
            + string.Join(',', item.Candidates.OrderBy(c => c.MapId)
                .Select(c => $"{c.MapId:D}:{c.FloorKey}:{c.ReferenceHash}:{c.IsPositive}")));
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
    }

    public async Task PruneModelHistoryAsync(CancellationToken cancellationToken)
    {
        var manifests = await LoadModelManifestsAsync(cancellationToken);
        var protectedVersions = new HashSet<string>(StringComparer.Ordinal)
        {
            ReadCurrentVersion() ?? string.Empty,
            ReadBestExperimentalVersion() ?? string.Empty,
            ReadLastKnownGoodVersion() ?? string.Empty
        };
        protectedVersions.UnionWith(manifests.Where(item => item.IsPinned)
            .Select(item => item.Version));
        protectedVersions.UnionWith(manifests
            .Where(item => item.State == MapModelVersionState.Stable)
            .OrderByDescending(item => item.CreatedAt)
            .Take(10)
            .Select(item => item.Version));
        protectedVersions.UnionWith(manifests
            .Where(item => item.State != MapModelVersionState.Stable)
            .OrderByDescending(item => item.CreatedAt)
            .Take(3)
            .Select(item => item.Version));

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!protectedVersions.Contains(manifest.Version))
                TryDeleteDirectory(GetModelDirectory(manifest.Version));
        }
    }
}
