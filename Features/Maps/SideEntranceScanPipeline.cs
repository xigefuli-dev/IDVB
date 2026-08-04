using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>侧门扫描的单条候选结果。</summary>
public sealed class SideEntranceScanCandidate
{
    public MapRecord Map { get; init; } = new();
    /// <summary>匹配所用的楼层键（通常为主楼层 key，如 "1f"）。</summary>
    public string FloorKey { get; init; } = string.Empty;
    /// <summary>TM_CCOEFF_NORMED 匹配得分 [0, 1]，越高越相似。</summary>
    public double MatchScore { get; init; }
    /// <summary>特征模板在捕获帧中的最佳匹配位置（屏幕像素）。</summary>
    public MapScreenRect MatchLocation { get; init; }

    /// <summary>
    /// 取得最佳匹配时模板所用的缩放倍率（识别图像素 → 实时帧像素）。
    /// 模板匹配本身不具备尺度不变性，所以缩放只能靠遍历若干倍率取峰值得出；
    /// 这个值就是胜出的那一档，也是对齐种子唯一可信的初始缩放来源。
    /// </summary>
    public double MatchScale { get; init; } = 1d;
}

/// <summary>
/// Keeps the map identity confirmed by the user immutable throughout the
/// side-entrance alignment chain. Structure registration may refine the
/// transform, but it must never replace the confirmed map with another scan
/// candidate.
/// </summary>
public readonly record struct SideEntranceMapSelection(
    Guid MapId,
    string FloorKey)
{
    public bool IsValid =>
        MapId != Guid.Empty
        && !string.IsNullOrWhiteSpace(FloorKey);

    public bool Matches(SideEntranceScanCandidate? candidate) =>
        IsValid
        && candidate is not null
        && candidate.Map.Id == MapId
        && string.Equals(
            candidate.FloorKey,
            FloorKey,
            StringComparison.Ordinal);

    public bool Matches(MapAlignmentSession? seed) =>
        IsValid
        && seed is not null
        && seed.MapId == MapId
        && string.Equals(
            seed.FloorKey,
            FloorKey,
            StringComparison.Ordinal);

    public bool Matches(
        Guid recognitionMapId,
        Guid resultMapId,
        string? resultFloor) =>
        IsValid
        && recognitionMapId == MapId
        && resultMapId == MapId
        && string.Equals(
            resultFloor,
            FloorKey,
            StringComparison.Ordinal);

    public bool Matches(
        SideEntranceScanCandidate? candidate,
        MapAlignmentSession? seed,
        Guid recognitionMapId,
        Guid resultMapId,
        string? resultFloor) =>
        Matches(candidate)
        && Matches(seed)
        && Matches(recognitionMapId, resultMapId, resultFloor);
}

/// <summary>
/// 侧门专属扫描管线：对捕获帧运行模板匹配，返回 TopK 候选地图。
/// 与双门管线并列，仅用于首次地图识别，对齐阶段仍由原有管线处理。
/// </summary>
public sealed class SideEntranceScanPipeline
{
    /// <summary>
    /// Creates a provisional alignment session from the side-entrance feature
    /// match. The feature template is cut from the recognition image, so its
    /// center gives us an initial scale and translation even when no gate is
    /// visible in the current frame. The normal alignment pipeline still has
    /// to validate/refine this seed before it can be committed.
    /// </summary>
    public static bool TryCreateAlignmentSeed(
        SideEntranceScanCandidate candidate,
        MapScreenRect viewportBounds,
        out MapAlignmentSession session,
        out string failureReason)
    {
        session = new MapAlignmentSession();
        failureReason = string.Empty;
        ArgumentNullException.ThrowIfNull(candidate);

        if (!viewportBounds.IsValid || !candidate.MatchLocation.IsValid)
        {
            failureReason = "侧门特征匹配位置或当前地图视口无效。";
            return false;
        }

        var profile = MapFloorRules.GetFloorProfile(
            candidate.Map,
            candidate.FloorKey);
        if (profile is null
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            failureReason = "侧门特征对应楼层缺少有效的识别图尺寸。";
            return false;
        }

        var referenceCenterX = profile.SideEntranceFeatureCenterX;
        var referenceCenterY = profile.SideEntranceFeatureCenterY;
        if (!double.IsFinite(referenceCenterX)
            || !double.IsFinite(referenceCenterY)
            || referenceCenterX <= 0d
            || referenceCenterY <= 0d)
        {
            var anchor = profile.FindAnchor("side-entrance");
            if (anchor?.Bounds?.IsValid is not true)
            {
                failureReason = "侧门特征缺少可用的参考中心点。";
                return false;
            }

            referenceCenterX = (anchor.Bounds.X + (anchor.Bounds.Width / 2d))
                * profile.RecognitionPixelWidth;
            referenceCenterY = (anchor.Bounds.Y + (anchor.Bounds.Height / 2d))
                * profile.RecognitionPixelHeight;
        }

        // 缩放必须来自扫描阶段的多尺度搜索。不能用
        // MatchLocation.Width / (radius * 2) 反推：MatchTemplate 返回的矩形
        // 尺寸就是模板尺寸本身，而模板又是按同一个 radius 从识别图裁出的，
        // 这个比值恒等于 1，与实际缩放无关。
        var scale = candidate.MatchScale;
        if (!double.IsFinite(scale)
            || scale < SideEntranceScanPipeline.MinimumScale
            || scale > SideEntranceScanPipeline.MaximumScale)
        {
            failureReason = "侧门特征无法生成有效的初始缩放。";
            return false;
        }

        var screenCenterX = viewportBounds.X + candidate.MatchLocation.CenterX;
        var screenCenterY = viewportBounds.Y + candidate.MatchLocation.CenterY;
        var offsetX = screenCenterX - (referenceCenterX * scale);
        var offsetY = screenCenterY - (referenceCenterY * scale);
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            failureReason = "侧门特征无法生成有效的初始位移。";
            return false;
        }

        var transform = new MapOverlayTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = referenceCenterX,
            ReferenceCenterY = referenceCenterY,
            ScreenCenterX = screenCenterX,
            ScreenCenterY = screenCenterY,
            ReferenceWidth = profile.RecognitionPixelWidth,
            ReferenceHeight = profile.RecognitionPixelHeight,
            OrientationDegrees = profile.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = 0d
        };

        session = new MapAlignmentSession
        {
            MapId = candidate.Map.Id,
            MapUpdatedAt = candidate.Map.UpdatedAt,
            FloorKey = candidate.FloorKey,
            LockedTransform = transform,
            BaselineGateScale = scale,
            LastConfidence = candidate.MatchScore,
            LastObservationConfidence = candidate.MatchScore,
            LastSuccessfulAt = DateTimeOffset.UtcNow,
            LastObservationAt = DateTimeOffset.UtcNow,
            HasGatePairLock = false,
            Mode = MapAlignmentTrackingMode.AuxiliaryAnchorTracking,
            SideEntranceScanPriorConfidence = candidate.MatchScore
        };
        return true;
    }

    /// <summary>允许的最小缩放（识别图 → 实时帧）。</summary>
    public const double MinimumScale = 0.55d;
    /// <summary>允许的最大缩放（识别图 → 实时帧）。</summary>
    public const double MaximumScale = 2.2d;
    /// <summary>粗搜索的相对步长；决定缩放网格的疏密。</summary>
    private const double CoarseScaleStep = 0.06d;
    /// <summary>细化阶段在粗峰值两侧各取的档数。</summary>
    private const int RefineStepsPerSide = 3;
    /// <summary>粗搜索的降采样倍率。</summary>
    private const int CoarsePyramidFactor = 4;
    /// <summary>
    /// 细化窗口在粗峰值四周额外留出的全分辨率像素。粗峰值来自
    /// 1/<see cref="CoarsePyramidFactor"/> 分辨率，本身带有约
    /// ±<see cref="CoarsePyramidFactor"/> 像素的量化误差；再叠加相邻缩放
    /// 档位造成的峰值漂移，取 4 倍降采样步长。
    /// </summary>
    private const int RefineSearchMarginPixels = CoarsePyramidFactor * 4;

    /// <summary>
    /// 对捕获帧执行多尺度模板匹配，返回按得分降序排列的前 topK 候选。
    /// </summary>
    /// <param name="capturedFrame">捕获的游戏地图区域（灰度或彩色均可）。</param>
    /// <param name="candidates">
    ///   候选列表：(地图记录, 楼层键, 预处理特征模板 Mat)。
    ///   调用方保持模板的生命周期管理。
    /// </param>
    /// <param name="topK">返回的最大候选数量，默认 5。</param>
    /// <returns>按 MatchScore 降序排列的候选列表（长度 ≤ topK）。</returns>
    public IReadOnlyList<SideEntranceScanCandidate> RunScan(
        Mat capturedFrame,
        IReadOnlyList<(MapRecord map, string floorKey, Mat featureTemplate)> candidates,
        int topK = 5)
    {
        ArgumentNullException.ThrowIfNull(capturedFrame);
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK < 1)
            topK = 1;
        if (capturedFrame.Empty() || candidates.Count == 0)
            return [];

        // 将捕获帧转为灰度，供模板匹配使用
        using var grayFrame = new Mat();
        if (capturedFrame.Channels() == 1)
            capturedFrame.CopyTo(grayFrame);
        else
            Cv2.CvtColor(capturedFrame, grayFrame, ColorConversionCodes.BGR2GRAY);

        var results = new List<SideEntranceScanCandidate>(candidates.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var (map, floorKey, template) in candidates)
        {
            if (template is null || template.Empty())
                continue;

            sw.Restart();
            var best = SearchScales(grayFrame, template, map, floorKey);
            if (best is not null)
                results.Add(best);
            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"侧门扫描 {map.SequenceNumber}#{floorKey} · {sw.Elapsed.TotalMilliseconds:F0}ms",
                elapsedMs: sw.Elapsed.TotalMilliseconds);
        }

        // 按得分降序，取前 topK
        results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
        return results.Count <= topK ? results : results.GetRange(0, topK);
    }

    /// <summary>粗搜索的峰值：缩放档位与降采样图上的匹配左上角。</summary>
    private readonly record struct CoarsePeak(double Scale, int X, int Y);

    /// <summary>
    /// 金字塔式缩放搜索。粗扫在 1/<see cref="CoarsePyramidFactor"/> 分辨率上
    /// 遍历整个缩放区间，定位峰值所在的档位**和位置**；再在全分辨率下围绕该
    /// 峰值以 1/4 步长细化，得到可用于对齐种子的缩放与位置。
    /// 之所以必须降采样：全分辨率遍历整个网格要跑上百次 MatchTemplate，
    /// 单张地图就会拖到秒级，29 张地图无法在一次扫描内完成。
    /// 细化也必须限制在粗峰值周围的窗口内 —— 全帧细化每张地图仍有 7 次全
    /// 分辨率匹配，29 张地图叠起来就是十秒级的等待。粗峰值已经把位置定到了
    /// ±<see cref="CoarsePyramidFactor"/> 像素，全帧重搜没有额外信息。
    /// </summary>
    private static SideEntranceScanCandidate? SearchScales(
        Mat grayFrame,
        Mat template,
        MapRecord map,
        string floorKey)
    {
        var peak = FindCoarsePeak(grayFrame, template);
        if (peak is null)
            return null;

        var refineStep = CoarseScaleStep / 4d;
        var maximumScale = peak.Value.Scale
            * (1d + (RefineStepsPerSide * refineStep));
        if (!TryBuildRefineWindow(
                grayFrame,
                template,
                peak.Value,
                maximumScale,
                out var window))
        {
            return null;
        }

        using var searchRegion = new Mat(grayFrame, window);
        var origin = new Point(window.X, window.Y);
        var best = Evaluate(
            searchRegion,
            origin,
            template,
            map,
            floorKey,
            peak.Value.Scale);
        for (var index = -RefineStepsPerSide; index <= RefineStepsPerSide; index++)
        {
            if (index == 0)
                continue;
            var scale = peak.Value.Scale * (1d + (index * refineStep));
            if (scale < MinimumScale || scale > MaximumScale)
                continue;
            var evaluated = Evaluate(
                searchRegion,
                origin,
                template,
                map,
                floorKey,
                scale);
            if (evaluated is not null
                && (best is null || evaluated.MatchScore > best.MatchScore))
            {
                best = evaluated;
            }
        }

        return best;
    }

    /// <summary>
    /// 把降采样图上的粗峰值换算回全分辨率，并向四周扩出足以容纳最大细化
    /// 尺度模板的窗口。窗口被裁剪到帧内；若帧本身装不下模板则返回 false。
    /// </summary>
    private static bool TryBuildRefineWindow(
        Mat grayFrame,
        Mat template,
        CoarsePeak peak,
        double maximumScale,
        out Rect window)
    {
        window = default;
        var widest = (int)Math.Round(template.Width * maximumScale);
        var tallest = (int)Math.Round(template.Height * maximumScale);
        // MatchTemplate 要求模板严格小于搜索图，等大只会产出 1×1 的平凡结果。
        if (widest >= grayFrame.Width || tallest >= grayFrame.Height)
            return false;

        var left = (peak.X * CoarsePyramidFactor) - RefineSearchMarginPixels;
        var top = (peak.Y * CoarsePyramidFactor) - RefineSearchMarginPixels;
        var width = widest + (RefineSearchMarginPixels * 2);
        var height = tallest + (RefineSearchMarginPixels * 2);
        // 先夹住尺寸，再夹住原点，保证窗口完整落在帧内且仍能容纳模板。
        width = Math.Min(width, grayFrame.Width);
        height = Math.Min(height, grayFrame.Height);
        left = Math.Clamp(left, 0, grayFrame.Width - width);
        top = Math.Clamp(top, 0, grayFrame.Height - height);
        window = new Rect(left, top, width, height);
        return true;
    }

    /// <summary>
    /// 在降采样图上遍历缩放网格，返回得分最高的那一档缩放及其匹配位置。
    /// 缩放是相对原始分辨率的，位置则是降采样图坐标；降采样只影响搜索成本
    /// 与位置精度，不影响缩放语义。
    /// </summary>
    private static CoarsePeak? FindCoarsePeak(Mat grayFrame, Mat template)
    {
        using var coarseFrame = new Mat();
        Cv2.Resize(
            grayFrame,
            coarseFrame,
            new Size(
                Math.Max(1, grayFrame.Width / CoarsePyramidFactor),
                Math.Max(1, grayFrame.Height / CoarsePyramidFactor)),
            0d,
            0d,
            InterpolationFlags.Area);

        CoarsePeak? bestPeak = null;
        var bestScore = double.NegativeInfinity;
        for (var scale = MinimumScale;
            scale <= MaximumScale;
            scale *= 1d + CoarseScaleStep)
        {
            var width = (int)Math.Round(
                template.Width * scale / CoarsePyramidFactor);
            var height = (int)Math.Round(
                template.Height * scale / CoarsePyramidFactor);
            if (width < 8 || height < 8
                || width >= coarseFrame.Width || height >= coarseFrame.Height)
            {
                continue;
            }

            using var scaled = new Mat();
            Cv2.Resize(
                template,
                scaled,
                new Size(width, height),
                0d,
                0d,
                InterpolationFlags.Area);
            using var resultMat = new Mat();
            Cv2.MatchTemplate(
                coarseFrame,
                scaled,
                resultMat,
                TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(resultMat, out _, out var maxVal, out _, out var maxLoc);
            if (double.IsFinite(maxVal) && maxVal > bestScore)
            {
                bestScore = maxVal;
                bestPeak = new CoarsePeak(scale, maxLoc.X, maxLoc.Y);
            }
        }

        return bestPeak;
    }

    /// <summary>
    /// 按给定缩放重采样模板后在 <paramref name="searchRegion"/> 内匹配一次。
    /// 模板放大用 <see cref="InterpolationFlags.Cubic"/>、缩小用
    /// <see cref="InterpolationFlags.Area"/>，避免缩小时的锯齿压低相关性得分。
    /// </summary>
    /// <param name="searchRegion">搜索区域（细化窗口，全分辨率）。</param>
    /// <param name="regionOrigin">
    ///   搜索区域左上角在完整帧中的坐标；匹配位置会加回该原点，
    ///   使返回的 MatchLocation 始终是帧坐标而非窗口内的相对坐标。
    /// </param>
    private static SideEntranceScanCandidate? Evaluate(
        Mat searchRegion,
        Point regionOrigin,
        Mat template,
        MapRecord map,
        string floorKey,
        double scale)
    {
        var width = (int)Math.Round(template.Width * scale);
        var height = (int)Math.Round(template.Height * scale);
        // 模板必须严格小于搜索图：等大时 MatchTemplate 只会产出 1×1 的平凡结果。
        if (width < 8 || height < 8
            || width >= searchRegion.Width || height >= searchRegion.Height)
        {
            return null;
        }

        using var scaled = new Mat();
        Cv2.Resize(
            template,
            scaled,
            new Size(width, height),
            0d,
            0d,
            scale >= 1d ? InterpolationFlags.Cubic : InterpolationFlags.Area);

        using var resultMat = new Mat();
        Cv2.MatchTemplate(
            searchRegion,
            scaled,
            resultMat,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(resultMat, out _, out var maxVal, out _, out var maxLoc);
        if (!double.IsFinite(maxVal))
            return null;

        return new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = floorKey,
            MatchScore = maxVal,
            MatchScale = scale,
            MatchLocation = new MapScreenRect(
                regionOrigin.X + maxLoc.X,
                regionOrigin.Y + maxLoc.Y,
                width,
                height)
        };
    }
}
