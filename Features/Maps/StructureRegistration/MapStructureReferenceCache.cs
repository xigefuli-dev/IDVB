using OpenCvSharp;
using System.Text.Json;
using IDVBuff.Pipeline;

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
    // 借出中的条目计数，以及淘汰时仍被借用、需延后释放的条目。
    private readonly Dictionary<CacheKey, int> _leaseCounts = new();
    private readonly Dictionary<CacheKey, MapStructureFeatures> _evictedWhileLeased = new();

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
        MapStructureGenerationTuning? generationTuning = null)
    {
        var generationFingerprint = NormalizeGeneration(generationTuning)
            .CacheFingerprint;
        var key = new CacheKey(
            mapId,
            updatedAt.UtcTicks,
            MapStructurePreprocessor.AlgorithmVersion,
            floor,
            generationFingerprint);
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
        MapStructureGenerationTuning? generationTuning = null)
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
            generationFingerprint);

        using var memoryLookup = MapOperationTraceAmbient.StartChild(
            "reference_cache_memory_lookup",
            MapOperationWaitKind.Io,
            mapId: mapId.ToString("D"),
            floorKey: floor);
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
        memoryLookup.Complete();

        var directory = Path.Combine(
            _rootDirectory,
            mapId.ToString("N"),
            $"{updatedAt.UtcTicks}-{floor}-{MapStructurePreprocessor.AlgorithmVersion}-{generationFingerprint}");
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
            generationTuning);
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
                        GenerationFingerprint = generationFingerprint,
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

    private MapStructureFeatures Remember(
        CacheKey key,
        MapStructureFeatures features)
    {
        using var distanceMap = MapOperationTraceAmbient.StartChild(
            "reference_distance_map",
            MapOperationWaitKind.Compute);
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
                    // 仍被租用的条目不能就地释放，否则借用方手上的 Mat 会失效；
                    // 转入延后释放队列，等最后一个借用者归还。
                    if (_leaseCounts.GetValueOrDefault(evictKey) > 0)
                        _evictedWhileLeased[evictKey] = evicted.Features;
                    else
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

    // internal（而非 private）：借用凭据的构造器要接收它，可访问性必须一致。
    // 嵌套在公开类型里但本身 internal，不会进入对外 API。
    internal readonly record struct CacheKey(
        Guid MapId,
        long UpdatedAtUtcTicks,
        int AlgorithmVersion,
        string Floor,
        string GenerationFingerprint);

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
/*
 * 文件职责：MapStructureReferenceCache。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
