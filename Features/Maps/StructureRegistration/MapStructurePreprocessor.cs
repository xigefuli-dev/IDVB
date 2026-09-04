using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Controls whether live structure preprocessing also computes local feature
/// descriptors. Edge-only registration paths do not consume descriptors and
/// should avoid paying the AKAZE extraction cost.
/// </summary>
public enum MapStructurePreprocessingProfile
{
    EdgesOnly,
    EdgesAndFeatures,
    PrebuiltStructureLine,
    NativeObservedStructureLine
}

internal static class MapStructurePreprocessingProfileExtensions
{
    public static bool IncludesDescriptors(
        this MapStructurePreprocessingProfile profile) =>
        profile == MapStructurePreprocessingProfile.EdgesAndFeatures;

    public static bool CanSatisfy(
        this MapStructurePreprocessingProfile available,
        MapStructurePreprocessingProfile requested) =>
        available.Satisfies(requested);

    public static bool Satisfies(
        this MapStructurePreprocessingProfile available,
        MapStructurePreprocessingProfile requested) =>
        available == requested
        || (available == MapStructurePreprocessingProfile.EdgesAndFeatures
            && requested == MapStructurePreprocessingProfile.EdgesOnly);
}

/// <summary>
/// Converts annotated reference maps and live explored-map ROIs into comparable
/// positive structure evidence. Missing live pixels remain unknown evidence.
/// </summary>
public sealed partial class MapStructurePreprocessor
{
    public const int AlgorithmVersion = 7;

    private static readonly object _cacheGate = new();
    private static string? _cachedReferencePath;
    private static MapStructureFeatures? _cachedReferenceFeatures;

    public static void ClearReferenceCache()
    {
        lock (_cacheGate)
        {
            _cachedReferenceFeatures?.Dispose();
            _cachedReferenceFeatures = null;
            _cachedReferencePath = null;
        }
    }

    public MapStructureFeatures Process(
        Mat source,
        MapStructureGenerationTuning? generationTuning = null)
    {
        using var referencePreprocess = MapOperationTraceAmbient.StartChild(
            "reference_preprocess",
            MapOperationWaitKind.Compute);
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: false,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures,
            generationTuning: generationTuning);
    }

    public MapStructureFeatures ProcessOrb(
        Mat source,
        MapStructureGenerationTuning? generationTuning = null)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: true,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures,
            generationTuning: generationTuning);
    }

    public MapStructureFeatures ProcessCachedReference(
        Mat source,
        string? referencePath,
        out PreprocessTiming timing,
        out bool cacheHit,
        MapStructureGenerationTuning? generationTuning = null)
    {
        using var referenceCache = MapOperationTraceAmbient.StartChild(
            "reference_cache_wait",
            MapOperationWaitKind.Io);
        timing = new PreprocessTiming();
        cacheHit = false;
        if (referencePath is not null)
        {
            lock (_cacheGate)
            {
                if (string.Equals(
                    _cachedReferencePath,
                    referencePath,
                    StringComparison.Ordinal))
                {
                    var existing = _cachedReferenceFeatures;
                    if (existing is not null
                        && !existing.Edges.Empty())
                    {
                        cacheHit = true;
                        // Return a clone so the caller can safely Dispose
                        // without invalidating the cached instance.
                        return existing.Clone();
                    }
                }
                _cachedReferenceFeatures?.Dispose();
                _cachedReferenceFeatures = null;
                _cachedReferencePath = null;
            }
        }

        var result = ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: true,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures,
            generationTuning: generationTuning);
        if (referencePath is not null)
        {
            lock (_cacheGate)
            {
                _cachedReferenceFeatures?.Dispose();
                _cachedReferenceFeatures = result;
                _cachedReferencePath = referencePath;
            }
            // Return a clone so the caller owns their copy and can
            // Dispose it independently. The cache keeps the original.
            return result.Clone();
        }
        // No caching path — ownership transfers directly to the caller.
        return result;
    }

    public MapStructureFeatures ProcessReference(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        MapStructureGenerationTuning? generationTuning = null,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures)
    {
        using var referencePreprocess = MapOperationTraceAmbient.StartChild(
            "reference_preprocess",
            MapOperationWaitKind.Compute);
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions,
            dynamicIgnoreRegions: null,
            new PreprocessTiming(),
            useOrb: false,
            profile: profile,
            generationTuning: generationTuning);
    }

    public static MapStructureFeatures UsePrebuiltStructureLine(Mat line) =>
        UseStructureLine(
            line,
            null,
            MapStructurePreprocessingProfile.PrebuiltStructureLine);

    public static MapStructureFeatures UseNativeObservedStructureLine(
        Mat line,
        Mat validMask)
    {
        if (validMask.Empty()
            || validMask.Type() != MatType.CV_8UC1
            || validMask.Size() != line.Size())
        {
            throw new InvalidDataException(
                "实时结构 IDVA 的 ValidMask 必须是同尺寸的 8 位灰度图。");
        }
        return UseStructureLine(
            line,
            validMask,
            MapStructurePreprocessingProfile.NativeObservedStructureLine);
    }

    private static MapStructureFeatures UseStructureLine(
        Mat line,
        Mat? rawVisibleMask,
        MapStructurePreprocessingProfile profile)
    {
        if (line.Empty() || line.Type() != MatType.CV_8UC1)
            throw new InvalidDataException("预制线图必须是非空的 8 位灰度图。");
        var edges = line.Clone();
        var half = new Mat();
        var quarter = new Mat();
        Cv2.Resize(edges, half, new Size(Math.Max(1, edges.Width / 2), Math.Max(1, edges.Height / 2)), 0d, 0d, InterpolationFlags.Nearest);
        Cv2.Resize(edges, quarter, new Size(Math.Max(1, edges.Width / 4), Math.Max(1, edges.Height / 4)), 0d, 0d, InterpolationFlags.Nearest);
        return new MapStructureFeatures(
            Mat.Zeros(edges.Size(), MatType.CV_8UC1).ToMat(),
            edges.Clone(),
            edges,
            normalizedGray: line.Clone(),
            edgePyramid: [edges.Clone(), half, quarter],
            repeatedRegionMask: Mat.Zeros(edges.Size(), MatType.CV_8UC1).ToMat(),
            diagnosticTiming: new PreprocessTiming
            {
                Profile = profile,
                DescriptorExtractionSkipped = true
            },
            rawVisibleMask: rawVisibleMask?.Clone());
    }

    /// <summary>
    /// Processes a calibrated live ROI and removes detached fixed UI elements
    /// before its non-zero bounds are used as the registration template.
    /// </summary>
    public MapStructureFeatures ProcessLiveRoi(Mat source) =>
        ProcessLiveRoi(source, null, null);

    public MapStructureFeatures ProcessLiveRoi(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        bool generateVisibleMask = false,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures,
        MapStructureGenerationTuning? generationTuning = null)
    {
        using var livePreprocess = MapOperationTraceAmbient.StartChild(
            "live_structure_preprocess",
            MapOperationWaitKind.Compute);
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            timing,
            // The immutable reference cache uses AKAZE/MLDB. Structure feature
            // voting requires identical descriptor type and width, so the
            // regular live path must use the same extractor.
            useOrb: false,
            generateVisibleMask: generateVisibleMask,
            profile: profile,
            generationTuning: generationTuning);
    }

    /// <summary>
    /// Produces the same cleaned live structure as ProcessLiveRoi while using
    /// AKAZE descriptors compatible with the immutable reference cache. VPSG
    /// consumes this path before any translation is known.
    /// </summary>
    public MapStructureFeatures ProcessLiveRoiAkaze(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null,
        IReadOnlyList<Rect>? dynamicIgnoreRegions = null,
        bool generateVisibleMask = false,
        MapStructureGenerationTuning? generationTuning = null) =>
        ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            new PreprocessTiming(),
            useOrb: false,
            generateVisibleMask: generateVisibleMask,
            profile: MapStructurePreprocessingProfile.EdgesAndFeatures,
            generationTuning: generationTuning);

    public MapStructureFeatures ProcessLiveRoiDiagnostic(
        Mat source,
        out PreprocessTiming timing,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures,
        MapStructureGenerationTuning? generationTuning = null) =>
        ProcessLiveRoiDiagnostic(
            source,
            null,
            null,
            out timing,
            profile: profile,
            generationTuning: generationTuning);

    public MapStructureFeatures ProcessLiveRoiDiagnostic(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        out PreprocessTiming timing,
        bool generateVisibleMask = false,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures,
        MapStructureGenerationTuning? generationTuning = null)
    {
        timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            timing,
            useOrb: false,
            generateVisibleMask: generateVisibleMask,
            profile: profile,
            generationTuning: generationTuning);
    }

    /// <summary>
    /// Upgrades a cached edge-only live extraction by computing only the
    /// AKAZE keypoints and descriptors. The expensive thresholding,
    /// connected-component cleanup and edge construction remain reusable.
    /// </summary>
    internal static MapStructureFeatures UpgradeLiveRoiWithDescriptors(
        MapStructureFeatures edgesOnly,
        out PreprocessTiming timing)
    {
        ArgumentNullException.ThrowIfNull(edgesOnly);
        if (edgesOnly.NormalizedGray.Empty()
            || edgesOnly.NuisanceMask.Empty()
            || edgesOnly.Edges.Empty())
        {
            throw new ArgumentException(
                "The cached live structure does not contain an upgradeable base extraction.",
                nameof(edgesOnly));
        }

        timing = edgesOnly.DiagnosticTiming?.Clone()
            ?? new PreprocessTiming
            {
                Profile = MapStructurePreprocessingProfile.EdgesOnly,
                DescriptorExtractionSkipped = true
            };
        var previousFeatureMilliseconds = timing.FeaturesMs;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var descriptors = new Mat();
        try
        {
            DetectFeatures(
                edgesOnly.NormalizedGray,
                edgesOnly.NuisanceMask,
                useOrb: false,
                descriptors,
                out var keyPoints);
            stopwatch.Stop();
            timing.Profile =
                MapStructurePreprocessingProfile.EdgesAndFeatures;
            timing.DescriptorExtractionSkipped = false;
            timing.FeaturesMs = stopwatch.Elapsed.TotalMilliseconds;
            timing.TotalMs = Math.Max(
                    0d,
                    timing.TotalMs - previousFeatureMilliseconds)
                + timing.FeaturesMs;

            return new MapStructureFeatures(
                edgesOnly.NuisanceMask.Clone(),
                edgesOnly.StructureMask.Clone(),
                edgesOnly.Edges.Clone(),
                edgesOnly.ReferenceDistanceMap?.Clone(),
                edgesOnly.ClippedReferenceDistanceMap?.Clone(),
                edgesOnly.ClippedDistancePixels,
                edgesOnly.NormalizedGray.Clone(),
                edgesOnly.EdgePyramid.Select(level => level.Clone()).ToArray(),
                keyPoints,
                descriptors,
                edgesOnly.RepeatedRegionMask.Clone(),
                timing,
                edgesOnly.RawVisibleMask?.Clone());
        }
        catch
        {
            descriptors.Dispose();
            throw;
        }
    }

}
/*
 * 文件职责：MapStructurePreprocessor。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
