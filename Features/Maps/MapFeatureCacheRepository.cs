using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed class MapFeatureCacheRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string _path;
    private MapFeatureCacheDocument _document = new();

    public MapFeatureCacheRepository(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "MapRuntime");
        _path = Path.Combine(root, "map-feature-cache.json");
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            MapFeatureCacheDocument loaded = new();
            if (File.Exists(_path))
            {
                try
                {
                    await using var stream = File.OpenRead(_path);
                    loaded = await JsonSerializer.DeserializeAsync<MapFeatureCacheDocument>(
                        stream,
                        SerializerOptions) ?? new MapFeatureCacheDocument();
                }
                catch (JsonException)
                {
                    loaded = new MapFeatureCacheDocument();
                }
                catch (IOException)
                {
                    loaded = new MapFeatureCacheDocument();
                }
            }
            if (loaded.SchemaVersion is not (1 or 2))
                loaded = new MapFeatureCacheDocument();
            loaded.Entries ??= [];
            loaded.Entries = loaded.Entries
                .Where(entry => entry?.IsValid is true)
                .GroupBy(entry => entry.Key)
                .Select(MigrateGroup)
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .OrderByDescending(entry => entry.Scale.UpdatedAt)
                .Take(1024)
                .ToList();
            lock (_stateGate)
                _document = loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGet(MapFeatureCacheKey key, out MapFeatureCacheEntry? entry)
    {
        lock (_stateGate)
        {
            entry = _document.Entries.FirstOrDefault(candidate => candidate.Key == key);
            return entry is not null;
        }
    }

    public async Task UpsertAsync(MapFeatureCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsValid)
            throw new ArgumentException("地图缓存条目无效。", nameof(entry));

        await _gate.WaitAsync();
        try
        {
            MapFeatureCacheDocument snapshot;
            lock (_stateGate)
            {
                _document.Entries.RemoveAll(candidate => candidate.Key == entry.Key);
                _document.Entries.Add(entry);
                _document.Entries = _document.Entries
                    .OrderByDescending(candidate => candidate.Scale.UpdatedAt)
                    .Take(1024)
                    .ToList();
                snapshot = new MapFeatureCacheDocument
                {
                    SchemaVersion = 2,
                    Entries = [.. _document.Entries]
                };
            }

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_path}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MapFeatureCacheEntry? MigrateGroup(
        IGrouping<MapFeatureCacheKey, MapFeatureCacheEntry> group)
    {
        var entries = group
            .OrderByDescending(entry => entry.Scale.UpdatedAt)
            .ToArray();
        if (entries.Length == 0)
            return null;

        var minimum = entries.Min(entry => entry.Scale.UniformScale);
        var maximum = entries.Max(entry => entry.Scale.UniformScale);
        if (minimum <= 0d || ((maximum - minimum) / minimum) > 0.015d)
            return null;

        var winner = entries[0];
        var weight = entries.Sum(entry => Math.Max(0.01d, entry.Scale.Confidence));
        winner.SchemaVersion = 2;
        winner.Scale.SchemaVersion = 2;
        winner.Scale.UniformScale = entries.Sum(entry =>
            entry.Scale.UniformScale * Math.Max(0.01d, entry.Scale.Confidence)) / weight;
        winner.Scale.SampleCount = entries.Sum(entry => entry.Scale.SampleCount);
        winner.Scale.Confidence = entries.Max(entry => entry.Scale.Confidence);
        winner.Scale.LastObservedDpi = entries
            .Select(entry => entry.Scale.LastObservedDpi)
            .FirstOrDefault(dpi => dpi > 0);
        return winner;
    }
}
