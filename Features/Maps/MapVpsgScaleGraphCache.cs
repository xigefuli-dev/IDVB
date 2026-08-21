using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapVpsgScaleGraphEdge(
    int FirstIndex,
    int SecondIndex,
    double ReferenceDistance,
    double ReferenceAngleDegrees);

public sealed class MapVpsgScaleGraph
{
    public int SchemaVersion { get; set; } = 1;
    public int ReferenceWidth { get; set; }
    public int ReferenceHeight { get; set; }
    public int KeyPointCount { get; set; }
    public List<MapVpsgScaleGraphEdge> Edges { get; set; } = [];

    public bool IsCompatible(Size size, int keyPointCount) =>
        SchemaVersion == 1
        && ReferenceWidth == size.Width
        && ReferenceHeight == size.Height
        && KeyPointCount == keyPointCount
        && Edges.Count > 0;
}

/// <summary>
/// Persists the translation-independent reference half of VPSG. The cache is
/// derived data and is invalidated by the map content fingerprint.
/// </summary>
public sealed class MapVpsgScaleGraphCache
{
    private const int GridSize = 4;
    private const int DirectionBuckets = 8;
    private const int MaximumEdgesPerFeature = 8;
    private const double MinimumReferenceDistance = 80d;
    private const double MaximumReferenceDistance = 700d;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly Dictionary<string, MapVpsgScaleGraph> _memory = [];

    public MapVpsgScaleGraphCache(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "MapAlignmentCache");
    }

    public MapVpsgScaleGraph GetOrCreate(
        MapRecord map,
        string floorKey,
        Size referenceSize,
        IReadOnlyList<KeyPoint> keyPoints)
    {
        var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(map);
        var memoryKey = $"{map.Id:N}|{fingerprint}|{floorKey}|{keyPoints.Count}";
        lock (_gate)
        {
            if (_memory.TryGetValue(memoryKey, out var cached)
                && cached.IsCompatible(referenceSize, keyPoints.Count))
            {
                return cached;
            }
        }

        var directory = Path.Combine(
            _rootDirectory,
            map.Id.ToString("N"),
            $"{map.UpdatedAt.UtcTicks}-{floorKey}-"
                + MapStructurePreprocessor.AlgorithmVersion);
        var path = Path.Combine(
            directory,
            $"vpsg-scale-graph-{fingerprint[..16]}.json");
        var graph = TryLoad(path, referenceSize, keyPoints.Count)
            ?? Build(referenceSize, keyPoints);
        TrySave(path, graph);
        lock (_gate)
            _memory[memoryKey] = graph;
        return graph;
    }

    private static MapVpsgScaleGraph? TryLoad(
        string path,
        Size referenceSize,
        int keyPointCount)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var graph = JsonSerializer.Deserialize<MapVpsgScaleGraph>(
                File.ReadAllText(path),
                SerializerOptions);
            return graph?.IsCompatible(referenceSize, keyPointCount) is true
                ? graph
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySave(string path, MapVpsgScaleGraph graph)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(graph, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // VPSG can rebuild the graph in memory when derived storage is
            // unavailable.
        }
    }

    internal static MapVpsgScaleGraph Build(
        Size referenceSize,
        IReadOnlyList<KeyPoint> keyPoints)
    {
        var edges = new List<MapVpsgScaleGraphEdge>();
        for (var firstIndex = 0; firstIndex < keyPoints.Count; firstIndex++)
        {
            var first = keyPoints[firstIndex];
            var firstCell = Cell(first.Pt, referenceSize);
            var candidates = new List<(MapVpsgScaleGraphEdge Edge, double Quality)>();
            for (var secondIndex = firstIndex + 1;
                 secondIndex < keyPoints.Count;
                 secondIndex++)
            {
                var second = keyPoints[secondIndex];
                if (Cell(second.Pt, referenceSize) == firstCell)
                    continue;
                var dx = second.Pt.X - first.Pt.X;
                var dy = second.Pt.Y - first.Pt.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance is < MinimumReferenceDistance
                    or > MaximumReferenceDistance)
                {
                    continue;
                }
                var angle = Math.Atan2(dy, dx) * 180d / Math.PI;
                candidates.Add((
                    new MapVpsgScaleGraphEdge(
                        firstIndex,
                        secondIndex,
                        distance,
                        angle),
                    first.Response + second.Response));
            }

            edges.AddRange(candidates
                .GroupBy(candidate => DirectionBucket(
                    candidate.Edge.ReferenceAngleDegrees))
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Quality)
                    .ThenByDescending(candidate =>
                        candidate.Edge.ReferenceDistance)
                    .First())
                .OrderByDescending(candidate => candidate.Quality)
                .Take(MaximumEdgesPerFeature)
                .Select(candidate => candidate.Edge));
        }

        return new MapVpsgScaleGraph
        {
            ReferenceWidth = referenceSize.Width,
            ReferenceHeight = referenceSize.Height,
            KeyPointCount = keyPoints.Count,
            Edges = edges
        };
    }

    private static (int X, int Y) Cell(Point2f point, Size size) =>
        (
            Math.Clamp((int)(point.X * GridSize / Math.Max(1, size.Width)), 0, GridSize - 1),
            Math.Clamp((int)(point.Y * GridSize / Math.Max(1, size.Height)), 0, GridSize - 1));

    private static int DirectionBucket(double angleDegrees)
    {
        var normalized = (angleDegrees + 360d) % 360d;
        return Math.Clamp(
            (int)(normalized / (360d / DirectionBuckets)),
            0,
            DirectionBuckets - 1);
    }
}
/*
 * 文件职责：MapVpsgScaleGraphCache。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
