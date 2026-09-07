using OpenCvSharp;
using System.Text.Json;
using IDVBuff.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Non-authoritative derived cache. It never writes into MapRepository or
/// changes maps.json.
/// </summary>
public sealed partial class MapStructureReferenceCache : IDisposable
{
    internal int ResidentCount
    {
        get
        {
            lock (_memoryGate)
                return _memoryCache.Count;
        }
    }

    /// <summary>
    /// Discards all cached reference features and releases unmanaged memory.
    /// Entries in both resident cache and deferred-eviction map are disposed,
    /// and all lease accounting is reset.
    /// </summary>
    public void Clear()
    {
        using var perfScope = RealtimePerformanceTracker.TrackScope("StructureCache.Clear", forceLog: true);
        lock (_memoryGate)
        {
            _generation++;
            foreach (var (_, (features, _)) in _memoryCache)
            {
                features.Dispose();
            }
            foreach (var features in _evictedWhileLeased.Values)
            {
                features.Dispose();
            }
            _memoryCache.Clear();
            _evictedWhileLeased.Clear();
            _leaseCounts.Clear();
            _lruList.Clear();
        }
    }

    public void InvalidateMaps(IReadOnlySet<Guid> mapIds)
    {
        if (mapIds.Count == 0)
            return;
        var dispose = new List<MapStructureFeatures>();
        lock (_memoryGate)
        {
            foreach (var key in _memoryCache.Keys
                .Where(key => mapIds.Contains(key.MapId))
                .ToArray())
            {
                var cached = _memoryCache[key];
                _memoryCache.Remove(key);
                _lruList.Remove(cached.Node);
                if (_leaseCounts.GetValueOrDefault(key) > 0)
                    _evictedWhileLeased[key] = cached.Features;
                else
                    dispose.Add(cached.Features);
            }
        }
        foreach (var features in dispose)
            features.Dispose();
    }

    private sealed class KeyPointDocument
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Size { get; set; }
        public float Angle { get; set; }
        public float Response { get; set; }
        public int Octave { get; set; }
        public int ClassId { get; set; }

        public static KeyPointDocument From(KeyPoint point) => new()
        {
            X = point.Pt.X,
            Y = point.Pt.Y,
            Size = point.Size,
            Angle = point.Angle,
            Response = point.Response,
            Octave = point.Octave,
            ClassId = point.ClassId
        };

        public KeyPoint ToKeyPoint() => new(
            X,
            Y,
            Size,
            Angle,
            Response,
            Octave,
            ClassId);
    }

    private static MapStructureGenerationTuning NormalizeGeneration(
        MapStructureGenerationTuning? generationTuning)
    {
        var normalized = generationTuning?.Clone() ?? new();
        normalized.Normalize();
        return normalized;
    }

    private static KeyPoint[] ReadKeyPoints(string path)
    {
        try
        {
            var documents = JsonSerializer.Deserialize<KeyPointDocument[]>(
                File.ReadAllText(path));
            return documents?.Select(document => document.ToKeyPoint()).ToArray()
                ?? [];
        }
        catch
        {
            return [];
        }
    }
}
