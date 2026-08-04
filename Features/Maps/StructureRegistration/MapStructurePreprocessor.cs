using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Converts annotated reference maps and live explored-map ROIs into comparable
/// positive structure evidence. Missing live pixels remain unknown evidence.
/// </summary>
public sealed class MapStructurePreprocessor
{
    public const int AlgorithmVersion = 5;

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

    public MapStructureFeatures Process(Mat source)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: false);
    }

    public MapStructureFeatures ProcessOrb(Mat source)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions: null,
            dynamicIgnoreRegions: null,
            timing,
            useOrb: true);
    }

    public MapStructureFeatures ProcessCachedReference(
        Mat source,
        string? referencePath,
        out PreprocessTiming timing,
        out bool cacheHit)
    {
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
            useOrb: true);
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
        IReadOnlyList<NormalizedRectangle>? ignoreRegions) =>
        ProcessCore(
            source,
            retainDominantStructureCluster: false,
            ignoreRegions,
            dynamicIgnoreRegions: null,
            new PreprocessTiming(),
            useOrb: false);

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
        bool generateVisibleMask = false)
    {
        var timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            timing,
            useOrb: true,
            generateVisibleMask: generateVisibleMask);
    }

    public MapStructureFeatures ProcessLiveRoiDiagnostic(
        Mat source,
        out PreprocessTiming timing) =>
        ProcessLiveRoiDiagnostic(source, null, null, out timing);

    public MapStructureFeatures ProcessLiveRoiDiagnostic(
        Mat source,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        out PreprocessTiming timing,
        bool generateVisibleMask = false)
    {
        timing = new PreprocessTiming();
        return ProcessCore(
            source,
            retainDominantStructureCluster: true,
            ignoreRegions,
            dynamicIgnoreRegions,
            timing,
            useOrb: true,
            generateVisibleMask: generateVisibleMask);
    }

    private static MapStructureFeatures ProcessCore(
        Mat source,
        bool retainDominantStructureCluster,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions,
        IReadOnlyList<Rect>? dynamicIgnoreRegions,
        PreprocessTiming timing,
        bool useOrb,
        bool generateVisibleMask = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("结构预处理不能处理空图像。", nameof(source));

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
                new Size(5, 5));
            using var openKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(3, 3));
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
                RetainDominantStructureCluster(structure);
            timing.StructureMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            using var canny = new Mat();
            using var gradient = new Mat();
            using var expandedStructure = new Mat();
            Cv2.Canny(blurred, canny, 35d, 110d);
            Cv2.MorphologyEx(structure, gradient, MorphTypes.Gradient, openKernel);
            Cv2.Dilate(structure, expandedStructure, openKernel, iterations: 1);
            Cv2.BitwiseAnd(canny, expandedStructure, canny);
            Cv2.BitwiseAnd(canny, ~nuisance, canny);
            var edges = new Mat();
            if (retainDominantStructureCluster)
                canny.CopyTo(edges);
            else
                Cv2.BitwiseOr(gradient, canny, edges);
            RemoveSmallComponents(edges, edgeMode: true);
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

            stopwatch.Restart();
            var validFeatureMask = new Mat();
            Cv2.BitwiseNot(nuisance, validFeatureMask);
            var descriptors = new Mat();
            KeyPoint[] keyPoints;
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
            }
            else
            {
                using var detector = AKAZE.Create();
                detector.DetectAndCompute(
                    normalizedGray,
                    validFeatureMask,
                    out keyPoints,
                    descriptors);
            }
            validFeatureMask.Dispose();
            timing.FeaturesMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            var edgePyramid = CreatePyramid(edges);
            timing.PyramidMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            var repeatedRegionMask = CreateRepeatedRegionMask(
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

    private static void RetainDominantStructureCluster(Mat binary)
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

        var keptLabels = new HashSet<int> { dominant.Label };
        var attachmentDistance = Math.Clamp(
            Math.Min(binary.Width, binary.Height) / 30,
            18,
            48);
        var minimumAttachedArea = Math.Max(
            24,
            (int)Math.Round(dominant.Area * 0.001d));
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

    private static void RemoveSmallComponents(Mat binary, bool edgeMode = false)
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
            return;

        var minimumArea = Math.Max(
            edgeMode ? 8 : 24,
            (int)Math.Round(binary.Width * binary.Height * (edgeMode ? 0.000005d : 0.00002d)));
        using var kept = Mat.Zeros(binary.Size(), MatType.CV_8UC1).ToMat();
        for (var label = 1; label < count; label++)
        {
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            if (area < minimumArea || (width < 3 && height < 3))
                continue;
            using var component = new Mat();
            Cv2.Compare(labels, label, component, CmpTypes.EQ);
            Cv2.BitwiseOr(kept, component, kept);
        }
        kept.CopyTo(binary);
    }

    private sealed record StructureComponent(
        int Label,
        int Area,
        Rect Bounds);
}

public sealed class PreprocessTiming
{
    public double ClaheBlurMs;
    public double NuisanceMaskMs;
    public double StructureMs;
    public double EdgesMs;
    public double FeaturesMs;
    public double PyramidMs;
    public double RepeatedMs;
    public double VisibleMaskMs;
    public double TotalMs;

    public object ToReport() => new
    {
        ClaheBlurMs,
        NuisanceMaskMs,
        StructureMs,
        EdgesMs,
        FeaturesMs,
        PyramidMs,
        RepeatedMs,
        VisibleMaskMs,
        TotalMs
    };
}
