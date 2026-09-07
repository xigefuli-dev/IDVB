using OpenCvSharp;
using System.Text.Json;
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
    /// Entries currently leased are moved to the deferred-eviction map and
    /// disposed when their lease is returned.
    /// </summary>
    public void Clear()
    {
        lock (_memoryGate)
        {
            foreach (var (key, (features, _)) in _memoryCache)
            {
                if (_leaseCounts.TryGetValue(key, out var count) && count > 0)
                {
                    _evictedWhileLeased[key] = features;
                }
                else
                {
                    features.Dispose();
                }
            }
            _memoryCache.Clear();
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
}
