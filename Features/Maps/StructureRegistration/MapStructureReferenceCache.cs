using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Non-authoritative derived cache. It never writes into MapRepository or
/// changes maps.json.
/// </summary>
public sealed class MapStructureReferenceCache : IDisposable
{
    private readonly string _rootDirectory;
    private readonly MapStructurePreprocessor _preprocessor;
    private readonly object _memoryGate = new();

    // 8-slot LRU cache: 支持多地图多楼层同时缓存
    // 典型场景: 2 地图 × 4 楼层 = 8 槽，避免频繁磁盘 I/O
    private const int MaxCacheSlots = 8;
    private readonly LinkedList<CacheKey> _lruList = new();
    private readonly Dictionary<CacheKey, (MapStructureFeatures Features, LinkedListNode<CacheKey> Node)> _memoryCache = new();

    // 性能统计
    private long _cacheHits;
    private long _cacheMisses;
    private long _diskLoads;
    private long _diskLoadMilliseconds;

    public MapStructureReferenceCache(
        MapStructurePreprocessor preprocessor,
        string? rootDirectory = null)
    {
        _preprocessor = preprocessor;
        _rootDirectory = rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "MapAlignmentCache");
    }

    internal (long Hits, long Misses, long DiskLoads) GetStatisticsForDiagnostics()
    {
        lock (_memoryGate)
            return (_cacheHits, _cacheMisses, _diskLoads);
    }

    public MapStructureFeatures GetOrCreate(
        Guid mapId,
        DateTimeOffset updatedAt,
        Mat referenceImage,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null,
        string floor = "1f")
    {
        var key = new CacheKey(
            mapId,
            updatedAt.UtcTicks,
            MapStructurePreprocessor.AlgorithmVersion,
            floor);

        lock (_memoryGate)
        {
            if (_memoryCache.TryGetValue(key, out var cached))
            {
                // 内存缓存命中：提升到 LRU 头部
                _lruList.Remove(cached.Node);
                _lruList.AddFirst(cached.Node);
                _cacheHits++;

                return cached.Features.Clone();
            }
            _cacheMisses++;
        }

        var directory = Path.Combine(
            _rootDirectory,
            mapId.ToString("N"),
            $"{updatedAt.UtcTicks}-{floor}-{MapStructurePreprocessor.AlgorithmVersion}");
        var nuisancePath = Path.Combine(directory, "nuisance-mask.png");
        var structurePath = Path.Combine(directory, "structure-mask.png");
        var edgesPath = Path.Combine(directory, "edges.png");
        var grayPath = Path.Combine(directory, "normalized-gray.png");
        var halfEdgesPath = Path.Combine(directory, "edges-half.png");
        var quarterEdgesPath = Path.Combine(directory, "edges-quarter.png");
        var descriptorsPath = Path.Combine(directory, "akaze-descriptors.png");
        var keyPointsPath = Path.Combine(directory, "akaze-keypoints.json");
        var repeatedPath = Path.Combine(directory, "repeated-regions.png");
        var distancePath = Path.Combine(directory, "distance-transform.tiff");

        var diskLoadTimer = System.Diagnostics.Stopwatch.StartNew();
        if (File.Exists(nuisancePath)
            && File.Exists(structurePath)
            && File.Exists(edgesPath)
            && File.Exists(grayPath)
            && File.Exists(halfEdgesPath)
            && File.Exists(quarterEdgesPath)
            && File.Exists(descriptorsPath)
            && File.Exists(keyPointsPath)
            && File.Exists(repeatedPath)
            && File.Exists(distancePath))
        {
            var nuisance = Cv2.ImRead(nuisancePath, ImreadModes.Grayscale);
            var structure = Cv2.ImRead(structurePath, ImreadModes.Grayscale);
            var edges = Cv2.ImRead(edgesPath, ImreadModes.Grayscale);
            var gray = Cv2.ImRead(grayPath, ImreadModes.Grayscale);
            var halfEdges = Cv2.ImRead(halfEdgesPath, ImreadModes.Grayscale);
            var quarterEdges = Cv2.ImRead(
                quarterEdgesPath,
                ImreadModes.Grayscale);
            var descriptors = Cv2.ImRead(
                descriptorsPath,
                ImreadModes.Grayscale);
            var repeated = Cv2.ImRead(repeatedPath, ImreadModes.Grayscale);
            var distance = Cv2.ImRead(
                distancePath,
                ImreadModes.AnyDepth | ImreadModes.Grayscale);
            var keyPoints = ReadKeyPoints(keyPointsPath);
            if (!nuisance.Empty()
                && !structure.Empty()
                && !edges.Empty()
                && !gray.Empty()
                && !halfEdges.Empty()
                && !quarterEdges.Empty()
                && !descriptors.Empty()
                && !repeated.Empty()
                && !distance.Empty()
                && structure.Size() == referenceImage.Size()
                && edges.Size() == referenceImage.Size()
                && gray.Size() == referenceImage.Size()
                && repeated.Size() == referenceImage.Size()
                && distance.Size() == referenceImage.Size())
            {
                diskLoadTimer.Stop();

                lock (_memoryGate)
                {
                    _diskLoads++;
                    _diskLoadMilliseconds += (long)diskLoadTimer.Elapsed.TotalMilliseconds;
                }

                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"结构缓存磁盘加载 floor={floor} loadMs={diskLoadTimer.Elapsed.TotalMilliseconds:F1}",
                    elapsedMs: diskLoadTimer.Elapsed.TotalMilliseconds,
                    details: new()
                    {
                        ["cacheHit"] = false,
                        ["diskLoad"] = true,
                        ["totalLoads"] = _diskLoads,
                        ["avgLoadMs"] = _diskLoads > 0 ? _diskLoadMilliseconds / (double)_diskLoads : 0
                    });

                return Remember(
                    key,
                    new MapStructureFeatures(
                        nuisance,
                        structure,
                        edges,
                        referenceDistanceMap: distance,
                        normalizedGray: gray,
                        edgePyramid: [edges.Clone(), halfEdges, quarterEdges],
                        keyPoints: keyPoints,
                        descriptors: descriptors,
                        repeatedRegionMask: repeated));
            }
            nuisance.Dispose();
            structure.Dispose();
            edges.Dispose();
            gray.Dispose();
            halfEdges.Dispose();
            quarterEdges.Dispose();
            descriptors.Dispose();
            repeated.Dispose();
            distance.Dispose();
        }

        var preprocessTimer = System.Diagnostics.Stopwatch.StartNew();
        var generated = _preprocessor.ProcessReference(
            referenceImage,
            ignoreRegions);
        preprocessTimer.Stop();

        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Warning,
            $"结构缓存未命中，现场预处理 floor={floor} preprocessMs={preprocessTimer.Elapsed.TotalMilliseconds:F1}",
            elapsedMs: preprocessTimer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["cacheHit"] = false,
                ["diskLoad"] = false,
                ["preprocessed"] = true
            });

        try
        {
            Directory.CreateDirectory(directory);
            Cv2.ImWrite(nuisancePath, generated.NuisanceMask);
            Cv2.ImWrite(structurePath, generated.StructureMask);
            Cv2.ImWrite(edgesPath, generated.Edges);
            Cv2.ImWrite(grayPath, generated.NormalizedGray);
            Cv2.ImWrite(halfEdgesPath, generated.EdgePyramid[1]);
            Cv2.ImWrite(quarterEdgesPath, generated.EdgePyramid[2]);
            if (!generated.Descriptors.Empty())
                Cv2.ImWrite(descriptorsPath, generated.Descriptors);
            else
            {
                using var emptyDescriptors =
                    Mat.Zeros(1, 1, MatType.CV_8UC1).ToMat();
                Cv2.ImWrite(
                    descriptorsPath,
                    emptyDescriptors);
            }
            Cv2.ImWrite(repeatedPath, generated.RepeatedRegionMask);
            Cv2.ImWrite(
                distancePath,
                generated.GetOrCreateReferenceDistanceMap());
            File.WriteAllText(
                keyPointsPath,
                JsonSerializer.Serialize(
                    generated.KeyPoints.Select(KeyPointDocument.From).ToArray(),
                    new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(
                Path.Combine(directory, "metadata.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        MapId = mapId,
                        MapUpdatedAt = updatedAt,
                        AlgorithmVersion = MapStructurePreprocessor.AlgorithmVersion,
                        Floor = floor,
                        Width = referenceImage.Width,
                        Height = referenceImage.Height
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // The cache is optional. A read-only or full cache directory must
            // not prevent in-memory registration.
        }
        return Remember(key, generated);
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

    private MapStructureFeatures Remember(
        CacheKey key,
        MapStructureFeatures features)
    {
        features.GetOrCreateReferenceDistanceMap();
        features.GetOrCreateClippedReferenceDistanceMap(12d);

        lock (_memoryGate)
        {
            // Another caller may have populated the same key while this
            // caller was loading or generating it outside the lock. Reuse the
            // resident entry instead of leaving a duplicate linked-list node
            // and leaking the newly-created feature set.
            if (_memoryCache.TryGetValue(key, out var existing))
            {
                features.Dispose();
                _lruList.Remove(existing.Node);
                _lruList.AddFirst(existing.Node);
                return existing.Features.Clone();
            }

            // 如果缓存已满，移除最旧的条目（LRU 尾部）
            if (_memoryCache.Count >= MaxCacheSlots && _lruList.Last is not null)
            {
                var evictKey = _lruList.Last.Value;
                if (_memoryCache.TryGetValue(evictKey, out var evicted))
                {
                    evicted.Features.Dispose();
                    _memoryCache.Remove(evictKey);
                    _lruList.RemoveLast();
                }
            }

            // 添加到 LRU 头部
            var node = _lruList.AddFirst(key);
            _memoryCache[key] = (features, node);

            return features.Clone();
        }
    }

    public void Dispose()
    {
        lock (_memoryGate)
        {
            foreach (var (features, _) in _memoryCache.Values)
            {
                features.Dispose();
            }
            _memoryCache.Clear();
            _lruList.Clear();

            if (_cacheHits + _cacheMisses > 0)
            {
                var hitRate = _cacheHits * 100.0 / (_cacheHits + _cacheMisses);
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"结构缓存统计 hits={_cacheHits} misses={_cacheMisses} hitRate={hitRate:F1}% " +
                    $"diskLoads={_diskLoads} avgLoadMs={(_diskLoads > 0 ? _diskLoadMilliseconds / (double)_diskLoads : 0):F1}");
            }
        }
    }

    private readonly record struct CacheKey(
        Guid MapId,
        long UpdatedAtUtcTicks,
        int AlgorithmVersion,
        string Floor);

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
