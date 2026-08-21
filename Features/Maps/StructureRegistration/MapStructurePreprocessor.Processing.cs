using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructurePreprocessor
{
    private static MapStructureFeatures ProcessCore(
        Mat source,
        bool retainDominantStructureCluster,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        PreprocessTiming timing,
        bool useOrb,
        bool generateVisibleMask = false,
        MapStructurePreprocessingProfile profile =
            MapStructurePreprocessingProfile.EdgesAndFeatures,
        MapStructureGenerationTuning? generationTuning = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("结构预处理不能处理空图像。", nameof(source));

        generationTuning = generationTuning?.Clone() ?? new();
        generationTuning.Normalize();
        timing.Profile = profile;
        timing.DescriptorExtractionSkipped = !profile.IncludesDescriptors();
        timing.GenerationFingerprint = generationTuning.CacheFingerprint;
        timing.EdgeComposition = retainDominantStructureCluster
            ? generationTuning.LiveEdgeComposition
            : generationTuning.ReferenceEdgeComposition;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var bgr = ToBgr(source);
        using var gray = new Mat();
        using var hsv = new Mat();
        using var blurred = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var normalizedGray = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2d, new Size(8, 8)))
            clahe.Apply(gray, normalizedGray);
        Cv2.GaussianBlur(normalizedGray, blurred, new Size(5, 5), 0d);
        timing.ClaheBlurMs = stopwatch.Elapsed.TotalMilliseconds;

        var channels = Cv2.Split(hsv);
        try
        {
            stopwatch.Restart();
            var nuisance = new Mat();
            using var saturated = new Mat();
            using var bright = new Mat();
            Cv2.Threshold(channels[1], saturated, 105d, 255d, ThresholdTypes.Binary);
            Cv2.Threshold(channels[2], bright, 70d, 255d, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(saturated, bright, nuisance);
            using var nuisanceKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(3, 3));
            Cv2.Dilate(nuisance, nuisance, nuisanceKernel, iterations: 1);
            ApplyIgnoreRegions(
                nuisance,
                ignoreRegions,
                dynamicIgnoreRegions);
            timing.NuisanceMaskMs = stopwatch.Elapsed.TotalMilliseconds;

            // ═══════════════════════════════════════════════════════
            // VisibleMask 生成（仅当显式开启时执行）
            // ═══════════════════════════════════════════════════════
            Mat? rawVisibleMask = null;
            if (generateVisibleMask)
            {
                stopwatch.Restart();

                // Step 1: 基础可见性 —— V > VisibleVMin
                using var aboveVMin = new Mat();
                Cv2.Threshold(
                    channels[2], aboveVMin,
                    42d, 255d, ThresholdTypes.Binary);

                // Step 2: 区分 UI/标记 和真正的地图地板
                // 可见 = V > VMin AND (S > SMin OR V > HighlightVMin)
                using var aboveSMin = new Mat();
                Cv2.Threshold(
                    channels[1], aboveSMin,
                    14d, 255d, ThresholdTypes.Binary);
                using var aboveHighlight = new Mat();
                Cv2.Threshold(
                    channels[2], aboveHighlight,
                    80d, 255d, ThresholdTypes.Binary);

                using var visibleBase = new Mat();
                Cv2.BitwiseOr(aboveSMin, aboveHighlight, visibleBase);
                Cv2.BitwiseAnd(aboveVMin, visibleBase, visibleBase);

                // Step 3: 形态学清理
                using var visibleKernel = Cv2.GetStructuringElement(
                    MorphShapes.Rect, new Size(3, 3));
                Cv2.MorphologyEx(
                    visibleBase, visibleBase,
                    MorphTypes.Close, visibleKernel);
                Cv2.MorphologyEx(
                    visibleBase, visibleBase,
                    MorphTypes.Open, visibleKernel);

                // Step 4: 排除 nuisance 和 ignore regions
                Cv2.BitwiseAnd(visibleBase, ~nuisance, visibleBase);
                ApplyIgnoreRegions(
                    visibleBase, ignoreRegions, dynamicIgnoreRegions);

                rawVisibleMask = visibleBase.Clone();
                timing.VisibleMaskMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            stopwatch.Restart();
            var structure = new Mat();
            Cv2.Threshold(
                blurred,
                structure,
                0d,
                255d,
                ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.BitwiseAnd(structure, ~nuisance, structure);
            RemoveSmallComponents(structure);
            using var closeKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(
                    generationTuning.StructureCloseKernelSize,
                    generationTuning.StructureCloseKernelSize));
            using var openKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(
                    generationTuning.StructureOpenKernelSize,
                    generationTuning.StructureOpenKernelSize));
            Cv2.MorphologyEx(structure, structure, MorphTypes.Close, closeKernel);
            Cv2.MorphologyEx(structure, structure, MorphTypes.Open, openKernel);
            var border = Math.Max(
                1,
                (int)Math.Round(Math.Min(source.Width, source.Height) * 0.02d));
            // Clear the capture frame before connected-component filtering.
            // Otherwise a bright one-pixel frame can join detached HUD
            // controls to the map around the image boundary, causing the
            // entire cluster to survive as one oversized query.
            Cv2.Rectangle(
                structure,
                new Rect(0, 0, structure.Width, structure.Height),
                Scalar.Black,
                border);
            if (retainDominantStructureCluster)
                RetainDominantStructureCluster(
                    structure,
                    timing,
                    generationTuning);
            timing.StructureMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            using var canny = new Mat();
            using var gradient = new Mat();
            using var expandedStructure = new Mat();
            Cv2.Canny(
                blurred,
                canny,
                generationTuning.CannyLowThreshold,
                generationTuning.CannyHighThreshold);
            Cv2.MorphologyEx(structure, gradient, MorphTypes.Gradient, openKernel);
            Cv2.Dilate(structure, expandedStructure, openKernel, iterations: 1);
            Cv2.BitwiseAnd(canny, expandedStructure, canny);
            Cv2.BitwiseAnd(canny, ~nuisance, canny);
            var edges = new Mat();
            var edgeComposition = retainDominantStructureCluster
                ? generationTuning.LiveEdgeComposition
                : generationTuning.ReferenceEdgeComposition;
            if (retainDominantStructureCluster
                && edgeComposition == MapStructureEdgeComposition.GradientAndCanny
                && generationTuning.LiveGradientSupportRadiusPixels > 0)
            {
                using var gradientSupport = new Mat();
                var supportDiameter =
                    (generationTuning.LiveGradientSupportRadiusPixels * 2) + 1;
                using var supportKernel = Cv2.GetStructuringElement(
                    MorphShapes.Ellipse,
                    new Size(supportDiameter, supportDiameter));
                Cv2.Dilate(canny, gradientSupport, supportKernel);
                Cv2.BitwiseAnd(gradient, gradientSupport, gradient);
            }
            if (edgeComposition == MapStructureEdgeComposition.CannyOnly)
                canny.CopyTo(edges);
            else
                Cv2.BitwiseOr(gradient, canny, edges);
            if (generationTuning.EdgeClosingIterations > 0)
            {
                using var edgeCloseKernel = Cv2.GetStructuringElement(
                    MorphShapes.Rect,
                    new Size(
                        generationTuning.EdgeClosingKernelSize,
                        generationTuning.EdgeClosingKernelSize));
                Cv2.MorphologyEx(
                    edges,
                    edges,
                    MorphTypes.Close,
                    edgeCloseKernel,
                    iterations: generationTuning.EdgeClosingIterations);
            }
            timing.EdgeComponentCount = RemoveSmallComponents(
                edges,
                edgeMode: true,
                minimumEdgeComponentArea:
                    generationTuning.MinimumEdgeComponentAreaPixels);
            timing.EdgesMs = stopwatch.Elapsed.TotalMilliseconds;

            Cv2.Rectangle(
                nuisance,
                new Rect(0, 0, nuisance.Width, nuisance.Height),
                Scalar.White,
                border);
            Cv2.Rectangle(
                edges,
                new Rect(0, 0, edges.Width, edges.Height),
                Scalar.Black,
                border);
            timing.EdgePixelCount = Cv2.CountNonZero(edges);

            var descriptors = new Mat();
            KeyPoint[] keyPoints = [];
            if (profile.IncludesDescriptors())
            {
                stopwatch.Restart();
                DetectFeatures(
                    normalizedGray,
                    nuisance,
                    useOrb,
                    descriptors,
                    out keyPoints);
                timing.FeaturesMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            stopwatch.Restart();
            var edgePyramid = CreatePyramid(edges);
            timing.PyramidMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            // Feature voting consults only the reference map's repeated-region
            // mask. Building a self-match mask for every live screenshot is
            // unused work and can be expensive on feature-dense frames.
            var repeatedRegionMask = retainDominantStructureCluster
                || !profile.IncludesDescriptors()
                ? Mat.Zeros(edges.Size(), MatType.CV_8UC1).ToMat()
                : CreateRepeatedRegionMask(
                    edges.Size(),
                    keyPoints,
                    descriptors);
            timing.RepeatedMs = stopwatch.Elapsed.TotalMilliseconds;

            timing.TotalMs = timing.ClaheBlurMs + timing.NuisanceMaskMs
                + timing.StructureMs + timing.EdgesMs + timing.FeaturesMs
                + timing.PyramidMs + timing.RepeatedMs + timing.VisibleMaskMs;

            return new MapStructureFeatures(
                nuisance,
                structure,
                edges,
                normalizedGray: normalizedGray,
                edgePyramid: edgePyramid,
                keyPoints: keyPoints,
                descriptors: descriptors,
                repeatedRegionMask: repeatedRegionMask,
                diagnosticTiming: timing,
                rawVisibleMask: rawVisibleMask);
        }
        catch
        {
            normalizedGray.Dispose();
            throw;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private static void ApplyIgnoreRegions(
        Mat nuisance,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions)
    {
        foreach (var region in ignoreRegions ?? [])
        {
            if (region?.IsValid is not true)
                continue;
            var left = Math.Clamp(
                (int)Math.Floor(region.X * nuisance.Width),
                0,
                nuisance.Width - 1);
            var top = Math.Clamp(
                (int)Math.Floor(region.Y * nuisance.Height),
                0,
                nuisance.Height - 1);
            var right = Math.Clamp(
                (int)Math.Ceiling((region.X + region.Width) * nuisance.Width),
                left + 1,
                nuisance.Width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling((region.Y + region.Height) * nuisance.Height),
                top + 1,
                nuisance.Height);
            Cv2.Rectangle(
                nuisance,
                new Rect(left, top, right - left, bottom - top),
                Scalar.White,
                -1);
        }

        foreach (var sourceRegion in dynamicIgnoreRegions ?? [])
        {
            if (sourceRegion.Width <= 0 || sourceRegion.Height <= 0)
                continue;
            const int padding = 6;
            var left = Math.Clamp(sourceRegion.X - padding, 0, nuisance.Width - 1);
            var top = Math.Clamp(sourceRegion.Y - padding, 0, nuisance.Height - 1);
            var right = Math.Clamp(
                sourceRegion.Right + padding,
                left + 1,
                nuisance.Width);
            var bottom = Math.Clamp(
                sourceRegion.Bottom + padding,
                top + 1,
                nuisance.Height);
            Cv2.Rectangle(
                nuisance,
                new Rect(left, top, right - left, bottom - top),
                Scalar.White,
                -1);
        }
    }

}
/*
 * 文件职责：MapStructurePreprocessor.Processing。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
