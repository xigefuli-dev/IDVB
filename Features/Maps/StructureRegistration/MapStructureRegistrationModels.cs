// Types split into:
//   MapStructureRegistrationModels.Enums.cs  – MapStructureRejectionReason, MapStructureEvidenceDisposition, MapStructureRejectionReasonExtensions
//   MapStructureRegistrationModels.Tuning.cs – MapStructureRegistrationTuning
//   MapStructureRegistrationModels.Request.cs – MapStructureRegistrationRequest
//   MapStructureRegistrationModels.Result.cs  – MapStructureCandidate, MapStructureRegistrationResult
//   MapStructureRegistrationModels.Detail.cs  – MapStructureConfidenceBreakdown, MapStructureConfidenceCalculator
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed class MapStructureFeatures : IDisposable
{
    private readonly object _visibleAwareCacheSync = new();
    private readonly Dictionary<int, Mat> _visibleAwareStructureCache = new();
    public MapStructureFeatures(
        Mat nuisanceMask,
        Mat structureMask,
        Mat edges,
        Mat? referenceDistanceMap = null,
        Mat? clippedReferenceDistanceMap = null,
        double? clippedDistancePixels = null,
        Mat? normalizedGray = null,
        IReadOnlyList<Mat>? edgePyramid = null,
        KeyPoint[]? keyPoints = null,
        Mat? descriptors = null,
        Mat? repeatedRegionMask = null,
        PreprocessTiming? diagnosticTiming = null,
        Mat? rawVisibleMask = null)
    {
        NuisanceMask = nuisanceMask;
        StructureMask = structureMask;
        Edges = edges;
        ReferenceDistanceMap = referenceDistanceMap;
        ClippedReferenceDistanceMap = clippedReferenceDistanceMap;
        ClippedDistancePixels = clippedDistancePixels;
        NormalizedGray = normalizedGray ?? new Mat();
        EdgePyramid = edgePyramid ?? [];
        KeyPoints = keyPoints ?? [];
        Descriptors = descriptors ?? new Mat();
        RepeatedRegionMask = repeatedRegionMask ?? Mat.Zeros(
            edges.Size(),
            MatType.CV_8UC1).ToMat();
        DiagnosticTiming = diagnosticTiming;
        RawVisibleMask = rawVisibleMask;
    }

    public Mat NuisanceMask { get; }
    public Mat StructureMask { get; }
    public Mat Edges { get; }
    public Mat? ReferenceDistanceMap { get; private set; }
    public Mat? ClippedReferenceDistanceMap { get; private set; }
    public double? ClippedDistancePixels { get; private set; }
    public Mat NormalizedGray { get; }
    public IReadOnlyList<Mat> EdgePyramid { get; }
    public KeyPoint[] KeyPoints { get; }
    public Mat Descriptors { get; }
    public Mat RepeatedRegionMask { get; }
    public PreprocessTiming? DiagnosticTiming { get; }
    public Mat? RawVisibleMask { get; }

    internal Mat GetOrCreateUnitStructureMask(int factor)
    {
        lock (_visibleAwareCacheSync)
        {
            if (_visibleAwareStructureCache.TryGetValue(factor, out var cached)) return cached;
            using var unit = new Mat();
            StructureMask.ConvertTo(unit, MatType.CV_32FC1, 1d / 255d);
            var result = new Mat();
            if (factor == 1) unit.CopyTo(result);
            else Cv2.Resize(unit, result,
                new Size(Math.Max(1, StructureMask.Width / factor), Math.Max(1, StructureMask.Height / factor)),
                0d, 0d, InterpolationFlags.Area);
            _visibleAwareStructureCache.Add(factor, result);
            return result;
        }
    }

    /// <summary>按需创建匹配用的腐蚀掩码。调用者负责释放。</summary>
    public Mat? CreateSafeVisibleMask(int erodePixels = 1)
    {
        if (RawVisibleMask is null || RawVisibleMask.Empty())
            return null;
        var safe = new Mat();
        var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(1 + erodePixels * 2, 1 + erodePixels * 2));
        Cv2.Erode(RawVisibleMask, safe, kernel);
        return safe;
    }

    public Mat GetOrCreateReferenceDistanceMap()
    {
        if (ReferenceDistanceMap is { } existing && !existing.Empty())
            return existing;
        using var inverse = new Mat();
        Cv2.BitwiseNot(Edges, inverse);
        var distance = new Mat();
        Cv2.DistanceTransform(
            inverse,
            distance,
            DistanceTypes.L2,
            DistanceTransformMasks.Precise);
        ReferenceDistanceMap = distance;
        return distance;
    }

    /// <summary>
    /// 返回按 <paramref name="clipPixels"/> 裁剪的参考距离图。返回的 Mat 由本
    /// 对象持有，调用方不得释放；且用**不同** clip 再次调用会释放并重建它，
    /// 使先前返回的引用失效。结构配准全程只用 tuning.DistanceClipPixels 这一个
    /// clip（Normalize 已把它钳在 [3,50]），据此可以在整轮搜索中安全持有引用。
    /// </summary>
    public Mat GetOrCreateClippedReferenceDistanceMap(double clipPixels)
    {
        if (ClippedReferenceDistanceMap is { } existing
            && !existing.Empty()
            && ClippedDistancePixels is { } existingClip
            && Math.Abs(existingClip - clipPixels) < 0.0001d)
        {
            return existing;
        }
        ClippedReferenceDistanceMap?.Dispose();
        var distance = GetOrCreateReferenceDistanceMap().Clone();
        Cv2.Min(distance, clipPixels, distance);
        ClippedReferenceDistanceMap = distance;
        ClippedDistancePixels = clipPixels;
        return distance;
    }

    public MapStructureFeatures Clone() => new(
        NuisanceMask.Clone(),
        StructureMask.Clone(),
        Edges.Clone(),
        ReferenceDistanceMap?.Clone(),
        ClippedReferenceDistanceMap?.Clone(),
        ClippedDistancePixels,
        NormalizedGray.Clone(),
        EdgePyramid.Select(level => level.Clone()).ToArray(),
        KeyPoints.ToArray(),
        Descriptors.Clone(),
        RepeatedRegionMask.Clone(),
        rawVisibleMask: RawVisibleMask?.Clone());

    public void Dispose()
    {
        lock (_visibleAwareCacheSync)
        {
            foreach (var cached in _visibleAwareStructureCache.Values) cached.Dispose();
            _visibleAwareStructureCache.Clear();
        }
        NuisanceMask.Dispose();
        StructureMask.Dispose();
        Edges.Dispose();
        ReferenceDistanceMap?.Dispose();
        ClippedReferenceDistanceMap?.Dispose();
        NormalizedGray.Dispose();
        foreach (var level in EdgePyramid)
            level.Dispose();
        Descriptors.Dispose();
        RepeatedRegionMask.Dispose();
        RawVisibleMask?.Dispose();
    }
}
/*
 * 文件职责：MapStructureRegistrationModels。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
