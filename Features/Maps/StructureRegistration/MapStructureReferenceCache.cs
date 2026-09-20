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
    private readonly string _rootDirectory;
    private readonly MapStructurePreprocessor _preprocessor;
    private readonly object _memoryGate = new();

    // A complete same-class scan historically verifies 9-13 candidates. Eight
    // slots therefore guaranteed disk churn between scans. Keep the cache
    // bounded, but large enough for the observed complete verification set.
    internal const int MaxCacheSlots = 16;
    internal const int MaxPrebuiltCacheSlots = 1;
    private readonly LinkedList<CacheKey> _lruList = new();
    private readonly Dictionary<CacheKey, (MapStructureFeatures Features, LinkedListNode<CacheKey> Node)> _memoryCache = new();
    // 借出中的条目计数，以及淘汰时仍被借用、需延后释放的条目。
    private readonly Dictionary<CacheKey, int> _leaseCounts = new();
    private readonly Dictionary<CacheKey, MapStructureFeatures> _evictedWhileLeased = new();

    private long _cacheHits;
    private long _cacheMisses;
    private long _diskLoads;
    private long _diskLoadMilliseconds;
    private long _generation;

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

    /// <summary>
    /// 借用常驻内存里的参考特征，命中时**不做拷贝**。
    /// <para>
    /// 参考特征对配准是只读的，但 <see cref="GetOrCreate"/> 为了让调用方能安全
    /// 释放，每次命中都要 <see cref="MapStructureFeatures.Clone"/> 深拷 11 张
    /// Mat——其中两张是全尺寸 CV_32F 距离图，1190×1012 一次约 14MB，实测均值
    /// 9~11ms（还不算 GC 压力）。租借把这笔开销降为零：条目寿命由缓存持有，
    /// 归还前不会被释放；LRU 淘汰到仍被租用的条目时延后到归还时才释放。
    /// </para>
    /// 磁盘层的完整性校验要拿参考图尺寸比对，所以不在这里做——未命中时调用方
    /// 自己解码参考图再走 <see cref="GetOrCreate"/>。
    /// </summary>
    public MapStructureFeaturesLease? TryRentResident(
        Guid mapId,
        DateTimeOffset updatedAt,
        string floor = "1f",
        MapStructureGenerationTuning? generationTuning = null,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures)
    {
        var generationFingerprint = NormalizeGeneration(generationTuning)
            .CacheFingerprint;
        var key = new CacheKey(
            mapId,
            updatedAt.UtcTicks,
            MapStructurePreprocessor.AlgorithmVersion,
            floor,
            generationFingerprint,
            profile);
        lock (_memoryGate)
        {
            if (!_memoryCache.TryGetValue(key, out var cached))
                return null;
            _lruList.Remove(cached.Node);
            _lruList.AddFirst(cached.Node);
            _cacheHits++;
            _leaseCounts[key] = _leaseCounts.GetValueOrDefault(key) + 1;
            return new MapStructureFeaturesLease(this, key, cached.Features);
        }
    }

    private void ReturnLease(CacheKey key)
    {
        MapStructureFeatures? toDispose = null;
        lock (_memoryGate)
        {
            var remaining = _leaseCounts.GetValueOrDefault(key) - 1;
            if (remaining > 0)
            {
                _leaseCounts[key] = remaining;
                return;
            }

            _leaseCounts.Remove(key);
            // 租用期间被 LRU 淘汰的条目，等最后一个借用者归还才真正释放。
            if (_evictedWhileLeased.Remove(key, out var evicted))
                toDispose = evicted;
        }
        toDispose?.Dispose();
    }

    /// <summary>
    /// 常驻参考特征的借用凭据。<see cref="Features"/> 由缓存持有，归还即失效，
    /// 借用方不得自行释放它，也不得在归还后继续引用。
    /// </summary>
    public sealed class MapStructureFeaturesLease : IDisposable
    {
        private readonly MapStructureReferenceCache _owner;
        private readonly CacheKey _key;
        private bool _returned;

        internal MapStructureFeaturesLease(
            MapStructureReferenceCache owner,
            CacheKey key,
            MapStructureFeatures features)
        {
            _owner = owner;
            _key = key;
            Features = features;
        }

        public MapStructureFeatures Features { get; }

        public void Dispose()
        {
            if (_returned)
                return;
            _returned = true;
            _owner.ReturnLease(_key);
        }
    }

    public MapStructureFeatures GetOrCreate(
        Guid mapId,
        DateTimeOffset updatedAt,
        Mat referenceImage,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null,
        string floor = "1f",
        MapStructureGenerationTuning? generationTuning = null,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures)
    {
        using var cacheRoute = MapOperationTraceAmbient.StartChild(
            "reference_cache_route",
            MapOperationWaitKind.Io,
            mapId: mapId.ToString("D"),
            floorKey: floor);
        generationTuning = NormalizeGeneration(generationTuning);
        var generationFingerprint = generationTuning.CacheFingerprint;
        var key = new CacheKey(
            mapId,
            updatedAt.UtcTicks,
            MapStructurePreprocessor.AlgorithmVersion,
            floor,
            generationFingerprint,
            profile);

        using var memoryLookup = MapOperationTraceAmbient.StartChild(
            "reference_cache_memory_lookup",
            MapOperationWaitKind.Io,
            mapId: mapId.ToString("D"),
            floorKey: floor);
        long generationSnapshot;
        lock (_memoryGate)
        {
            generationSnapshot = _generation;
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
        memoryLookup.Complete();

        if (profile == MapStructurePreprocessingProfile.PrebuiltStructureLine)
            return Remember(key, MapStructurePreprocessor.UsePrebuiltStructureLine(referenceImage), generationSnapshot);

        var directory = Path.Combine(
            _rootDirectory,
            mapId.ToString("N"),
            $"{updatedAt.UtcTicks}-{floor}-{MapStructurePreprocessor.AlgorithmVersion}-{generationFingerprint}-{profile}");
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

        var diskLoadSpan = MapOperationTraceAmbient.StartChild(
            "reference_cache_disk_read",
            MapOperationWaitKind.Io,
            mapId: mapId.ToString("D"),
            floorKey: floor);
        var diskLoadTimer = System.Diagnostics.Stopwatch.StartNew();
        var requiresDescriptors = profile.IncludesDescriptors();
        if (File.Exists(nuisancePath)
            && File.Exists(structurePath)
            && File.Exists(edgesPath)
            && File.Exists(grayPath)
            && File.Exists(halfEdgesPath)
            && File.Exists(quarterEdgesPath)
            && (!requiresDescriptors
                || (File.Exists(descriptorsPath)
                    && File.Exists(keyPointsPath)))
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
            var descriptors = requiresDescriptors
                ? Cv2.ImRead(descriptorsPath, ImreadModes.Grayscale)
                : new Mat();
            var repeated = Cv2.ImRead(repeatedPath, ImreadModes.Grayscale);
            var distance = Cv2.ImRead(
                distancePath,
                ImreadModes.AnyDepth | ImreadModes.Grayscale);
            var keyPoints = requiresDescriptors
                ? ReadKeyPoints(keyPointsPath)
                : [];
            if (!nuisance.Empty()
                && !structure.Empty()
                && !edges.Empty()
                && !gray.Empty()
                && !halfEdges.Empty()
                && !quarterEdges.Empty()
                && (!requiresDescriptors || !descriptors.Empty())
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

                diskLoadSpan.Complete();

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
        diskLoadSpan.Complete();

        var preprocessTimer = System.Diagnostics.Stopwatch.StartNew();
        var generated = _preprocessor.ProcessReference(
            referenceImage,
            ignoreRegions,
            generationTuning,
            profile);
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

        using var cacheWrite = MapOperationTraceAmbient.StartChild(
            "reference_cache_write",
            MapOperationWaitKind.Io,
            mapId: mapId.ToString("D"),
            floorKey: floor);
        try
        {
            Directory.CreateDirectory(directory);
            Cv2.ImWrite(nuisancePath, generated.NuisanceMask);
            Cv2.ImWrite(structurePath, generated.StructureMask);
            Cv2.ImWrite(edgesPath, generated.Edges);
            Cv2.ImWrite(grayPath, generated.NormalizedGray);
            Cv2.ImWrite(halfEdgesPath, generated.EdgePyramid[1]);
            Cv2.ImWrite(quarterEdgesPath, generated.EdgePyramid[2]);
            if (profile.IncludesDescriptors() && !generated.Descriptors.Empty())
                Cv2.ImWrite(descriptorsPath, generated.Descriptors);
            else if (profile.IncludesDescriptors())
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
                        GenerationFingerprint = generationFingerprint,
                        Profile = profile.ToString(),
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
        return Remember(key, generated, generationSnapshot);
    }

    private MapStructureFeatures Remember(
        CacheKey key,
        MapStructureFeatures features,
        long expectedGeneration = 0)
    {
        using var distanceMap = MapOperationTraceAmbient.StartChild(
            "reference_distance_map",
            MapOperationWaitKind.Compute);
        features.GetOrCreateReferenceDistanceMap();
        features.GetOrCreateClippedReferenceDistanceMap(12d);

        lock (_memoryGate)
        {
            if (expectedGeneration != 0 && _generation != expectedGeneration)
            {
                // 缓存代次已改变（对局已重置并调用了 Clear()），放弃将此孤儿特征存入常驻缓存，
                // 直接返回未缓存的特征供当前调用方使用或释放，防止内存常驻泄漏。
                return features;
            }

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

            // 预制线图含两张全尺寸 CV_32F 距离图，只保留当前候选供 VPSG 复用。
            var maxCacheSlots = key.Profile ==
                MapStructurePreprocessingProfile.PrebuiltStructureLine
                    ? MaxPrebuiltCacheSlots
                    : MaxCacheSlots;
            while (_memoryCache.Count >= maxCacheSlots
                && _lruList.Last is not null)
            {
                var evictKey = _lruList.Last.Value;
                if (_memoryCache.TryGetValue(evictKey, out var evicted))
                {
                    // 仍被租用的条目不能就地释放，否则借用方手上的 Mat 会失效；
                    // 转入延后释放队列，等最后一个借用者归还。
                    if (_leaseCounts.GetValueOrDefault(evictKey) > 0)
                        _evictedWhileLeased[evictKey] = evicted.Features;
                    else
                        evicted.Features.Dispose();
                    _memoryCache.Remove(evictKey);
                }
                _lruList.RemoveLast();
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
            _generation++;
            foreach (var (features, _) in _memoryCache.Values)
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

    // 借用凭据的构造器要接收它，所以保持 internal。
    internal readonly record struct CacheKey(
        Guid MapId,
        long UpdatedAtUtcTicks,
        int AlgorithmVersion,
        string Floor,
        string GenerationFingerprint,
        MapStructurePreprocessingProfile Profile);
}
/* 文件职责：MapStructureReferenceCache，负责地图结构特征注册、候选评估与验证。 */
