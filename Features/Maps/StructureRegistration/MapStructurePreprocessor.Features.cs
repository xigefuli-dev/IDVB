using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructurePreprocessor
{
    private static void DetectFeatures(
        Mat normalizedGray,
        Mat nuisanceMask,
        bool useOrb,
        Mat descriptors,
        out KeyPoint[] keyPoints)
    {
        using var validFeatureMask = new Mat();
        Cv2.BitwiseNot(nuisanceMask, validFeatureMask);
        if (useOrb)
        {
            using var detector = ORB.Create(
                nFeatures: 1200,
                scaleFactor: 1.2f,
                nLevels: 8,
                edgeThreshold: 31,
                firstLevel: 0,
                scoreType: ORBScoreType.Harris,
                patchSize: 31,
                fastThreshold: 20);
            detector.DetectAndCompute(
                normalizedGray,
                validFeatureMask,
                out keyPoints,
                descriptors);
            return;
        }

        using var akaze = AKAZE.Create();
        akaze.DetectAndCompute(
            normalizedGray,
            validFeatureMask,
            out keyPoints,
            descriptors);
    }

    private static IReadOnlyList<Mat> CreatePyramid(Mat edges)
    {
        var half = new Mat();
        var quarter = new Mat();
        Cv2.Resize(
            edges,
            half,
            new Size(
                Math.Max(1, edges.Width / 2),
                Math.Max(1, edges.Height / 2)),
            interpolation: InterpolationFlags.Area);
        Cv2.Resize(
            edges,
            quarter,
            new Size(
                Math.Max(1, edges.Width / 4),
                Math.Max(1, edges.Height / 4)),
            interpolation: InterpolationFlags.Area);
        return [edges.Clone(), half, quarter];
    }

    private static Mat CreateRepeatedRegionMask(
        Size size,
        IReadOnlyList<KeyPoint> keyPoints,
        Mat descriptors)
    {
        var repeated = Mat.Zeros(size, MatType.CV_8UC1).ToMat();
        if (keyPoints.Count < 3 || descriptors.Empty())
            return repeated;
        try
        {
            using var matcher = new BFMatcher(NormTypes.Hamming);
            var matches = matcher.KnnMatch(descriptors, descriptors, 3);
            foreach (var group in matches)
            {
                var nonSelfMatches = group
                    .Where(match => match.QueryIdx != match.TrainIdx)
                    .OrderBy(match => match.Distance)
                    .Take(1)
                    .ToArray();
                if (nonSelfMatches.Length == 0
                    || nonSelfMatches[0].Distance > 18d)
                    continue;
                var nonSelf = nonSelfMatches[0];
                var point = keyPoints[nonSelf.QueryIdx].Pt;
                Cv2.Circle(
                    repeated,
                    new Point(
                        (int)Math.Round(point.X),
                        (int)Math.Round(point.Y)),
                    18,
                    Scalar.White,
                    -1);
            }
        }
        catch (OpenCVException)
        {
            repeated.SetTo(Scalar.Black);
        }
        return repeated;
    }

    private static Mat ToBgr(Mat source)
    {
        var bgr = new Mat();
        switch (source.Channels())
        {
            case 4:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                source.CopyTo(bgr);
                break;
            default:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                break;
        }
        return bgr;
    }

    private static void RetainDominantStructureCluster(
        Mat binary,
        PreprocessTiming timing,
        MapStructureGenerationTuning generationTuning)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            binary,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8);
        timing.StructureComponentCount = Math.Max(0, count - 1);
        if (count <= 1)
            return;

        var components = Enumerable.Range(1, count - 1)
            .Select(label => new StructureComponent(
                label,
                stats.At<int>(label, (int)ConnectedComponentsTypes.Area),
                new Rect(
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Top),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Width),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Height))))
            .ToArray();
        var minimumDominantArea = Math.Max(
            500,
            (int)Math.Round(binary.Width * binary.Height * 0.002d));
        var dominant = components
            .Where(component =>
                component.Area >= minimumDominantArea
                && component.Bounds.Width >= 40
                && component.Bounds.Height >= 40)
            .MaxBy(component => component.Area);
        if (dominant is null)
        {
            binary.SetTo(Scalar.Black);
            return;
        }
        timing.DominantComponentArea = dominant.Area;
        timing.DominantComponentX = dominant.Bounds.X;
        timing.DominantComponentY = dominant.Bounds.Y;
        timing.DominantComponentWidth = dominant.Bounds.Width;
        timing.DominantComponentHeight = dominant.Bounds.Height;

        var keptLabels = new HashSet<int> { dominant.Label };
        var attachmentDistance = generationTuning
            .DominantClusterAttachmentDistancePixels > 0
            ? generationTuning.DominantClusterAttachmentDistancePixels
            : Math.Clamp(
                Math.Min(binary.Width, binary.Height) / 30,
                18,
                48);
        var minimumAttachedArea = Math.Max(
            24,
            (int)Math.Round(
                dominant.Area
                * generationTuning.DominantClusterMinimumAttachedAreaRatio));
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var component in components)
            {
                if (keptLabels.Contains(component.Label)
                    || component.Area < minimumAttachedArea)
                {
                    continue;
                }
                if (!components.Any(existing =>
                        keptLabels.Contains(existing.Label)
                        && RectangleDistance(
                            existing.Bounds,
                            component.Bounds) <= attachmentDistance))
                {
                    continue;
                }
                keptLabels.Add(component.Label);
                changed = true;
            }
        }

        using var kept = Mat.Zeros(binary.Size(), MatType.CV_8UC1).ToMat();
        foreach (var label in keptLabels)
        {
            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(kept, component, kept);
        }
        kept.CopyTo(binary);
        timing.KeptStructureComponentCount = keptLabels.Count;
        using var keptPoints = new Mat();
        Cv2.FindNonZero(kept, keptPoints);
        if (!keptPoints.Empty())
        {
            var keptBounds = Cv2.BoundingRect(keptPoints);
            timing.KeptStructureBoundsX = keptBounds.X;
            timing.KeptStructureBoundsY = keptBounds.Y;
            timing.KeptStructureBoundsWidth = keptBounds.Width;
            timing.KeptStructureBoundsHeight = keptBounds.Height;
        }
    }

    private static double RectangleDistance(Rect first, Rect second)
    {
        var horizontal = Math.Max(
            0,
            Math.Max(first.Left - second.Right, second.Left - first.Right));
        var vertical = Math.Max(
            0,
            Math.Max(first.Top - second.Bottom, second.Top - first.Bottom));
        return Math.Sqrt((horizontal * horizontal) + (vertical * vertical));
    }

    private static int RemoveSmallComponents(
        Mat binary,
        bool edgeMode = false,
        int? minimumEdgeComponentArea = null)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            binary,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8);
        if (count <= 1)
            return Math.Max(0, count - 1);

        var minimumArea = Math.Max(
            edgeMode
                ? minimumEdgeComponentArea ?? 8
                : 24,
            (int)Math.Round(binary.Width * binary.Height * (edgeMode ? 0.000005d : 0.00002d)));
        using var kept = Mat.Zeros(binary.Size(), MatType.CV_8UC1).ToMat();
        var keptCount = 0;
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            if (area < minimumArea || (width < 3 && height < 3))
                continue;
            keptCount++;
            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(kept, component, kept);
        }
        kept.CopyTo(binary);
        return keptCount;
    }

    private sealed record StructureComponent(
        int Label,
        int Area,
        Rect Bounds);
}

public sealed class PreprocessTiming
{
    public MapStructurePreprocessingProfile Profile =
        MapStructurePreprocessingProfile.EdgesAndFeatures;
    public bool DescriptorExtractionSkipped;
    public string GenerationFingerprint = string.Empty;
    public MapStructureEdgeComposition EdgeComposition =
        MapStructureEdgeComposition.GradientAndCanny;
    public double ClaheBlurMs;
    public double NuisanceMaskMs;
    public double StructureMs;
    public double EdgesMs;
    public double FeaturesMs;
    public double PyramidMs;
    public double RepeatedMs;
    public double VisibleMaskMs;
    public double TotalMs;
    public int StructureComponentCount;
    public int KeptStructureComponentCount;
    public int DominantComponentArea;
    public int DominantComponentX;
    public int DominantComponentY;
    public int DominantComponentWidth;
    public int DominantComponentHeight;
    public int KeptStructureBoundsX;
    public int KeptStructureBoundsY;
    public int KeptStructureBoundsWidth;
    public int KeptStructureBoundsHeight;
    public int EdgePixelCount;
    public int EdgeComponentCount;

    public PreprocessTiming Clone() => (PreprocessTiming)MemberwiseClone();

    public object ToReport() => new
    {
        Profile,
        DescriptorExtractionSkipped,
        GenerationFingerprint,
        EdgeComposition,
        ClaheBlurMs,
        NuisanceMaskMs,
        StructureMs,
        EdgesMs,
        FeaturesMs,
        PyramidMs,
        RepeatedMs,
        VisibleMaskMs,
        TotalMs,
        StructureComponentCount,
        KeptStructureComponentCount,
        DominantComponentArea,
        DominantComponentX,
        DominantComponentY,
        DominantComponentWidth,
        DominantComponentHeight,
        KeptStructureBoundsX,
        KeptStructureBoundsY,
        KeptStructureBoundsWidth,
        KeptStructureBoundsHeight,
        EdgePixelCount,
        EdgeComponentCount
    };
}
/*
 * 文件职责：MapStructurePreprocessor.Features。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
