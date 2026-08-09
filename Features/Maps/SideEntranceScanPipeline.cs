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
/// The side-entrance scan result keeps the mandatory gate observation next to
/// the feature-region candidates. The gate is scan evidence; it is not an
/// alignment result and must be validated again by the selected-map alignment
/// pipeline.
/// </summary>
public sealed class SideEntranceScanResult
{
    public GateDetectionResult GateDetection { get; init; } = new();
    public IReadOnlyList<SideEntranceScanCandidate> Candidates { get; init; } = [];
    public string FailureReason { get; init; } = string.Empty;

    public GateDetection? Gate => GateDetection.Gates
        .OrderByDescending(gate => gate.Score)
        .FirstOrDefault();
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
    /// Creates the provisional lock used between side-gate scan selection and
    /// selected-map alignment. The transform comes from the multi-scale side
    /// feature match; gate icon measurements are kept only as evidence for the
    /// next gate search because UI icon size and map-image scale are different
    /// coordinate systems.
    /// </summary>
    public static bool TryCreateGateAlignmentSeed(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewportBounds,
        double referenceGateIconWidth,
        double referenceGateIconHeight,
        out MapAlignmentSession session,
        out string failureReason)
    {
        session = new MapAlignmentSession();
        failureReason = string.Empty;
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(gate);

        if (!viewportBounds.IsValid
            || !gate.ScreenBounds.IsValid
            || !candidate.MatchLocation.IsValid)
        {
            failureReason = "side-gate scan did not produce a valid viewport or gate.";
            return false;
        }

        var profile = MapFloorRules.GetFloorProfile(
            candidate.Map,
            candidate.FloorKey);
        if (profile is null
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            failureReason = "the selected side-gate candidate has no valid recognition dimensions.";
            return false;
        }

        var sideAnchor = profile.FindAnchor("side-entrance");
        if (sideAnchor?.Bounds?.IsValid is not true)
        {
            failureReason = "the selected map has no marked side-entrance anchor.";
            return false;
        }

        var referenceBounds = new MapScreenRect(
            sideAnchor.Bounds.X * profile.RecognitionPixelWidth,
            sideAnchor.Bounds.Y * profile.RecognitionPixelHeight,
            sideAnchor.Bounds.Width * profile.RecognitionPixelWidth,
            sideAnchor.Bounds.Height * profile.RecognitionPixelHeight);

        // The side feature is cut from the map recognition image, so its
        // searched scale is the map-image scale. Gate icons can remain nearly
        // constant-sized while the map is zoomed and must not replace it.
        var mapScale = candidate.MatchScale;
        if (!double.IsFinite(mapScale)
            || mapScale < MinimumScale
            || mapScale > MaximumScale)
        {
            failureReason = "the side-gate scan produced an invalid map scale.";
            return false;
        }

        // Keep the exact center used by the persisted side-feature crop. In
        // particular, a legacy map may have had its crop center clamped at an
        // image edge; recomputing it from the anchor rectangle would shift the
        // provisional transform before structure alignment gets a chance to
        // refine it.
        var referenceCenterX = profile.SideEntranceFeatureCenterX;
        var referenceCenterY = profile.SideEntranceFeatureCenterY;
        if (!double.IsFinite(referenceCenterX)
            || referenceCenterX <= 0d
            || !double.IsFinite(referenceCenterY)
            || referenceCenterY <= 0d)
        {
            referenceCenterX = referenceBounds.CenterX;
            referenceCenterY = referenceBounds.CenterY;
        }

        var screenCenterX = viewportBounds.X + candidate.MatchLocation.CenterX;
        var screenCenterY = viewportBounds.Y + candidate.MatchLocation.CenterY;
        var offsetX = screenCenterX - (referenceCenterX * mapScale);
        var offsetY = screenCenterY - (referenceCenterY * mapScale);
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            failureReason = "the side-gate scan produced an invalid map translation.";
            return false;
        }

        var transform = new MapOverlayTransform
        {
            ScaleX = mapScale,
            ScaleY = mapScale,
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

        var priorConfidence = Math.Clamp(candidate.MatchScore, 0d, 1d);
        session = new MapAlignmentSession
        {
            MapId = candidate.Map.Id,
            MapUpdatedAt = candidate.Map.UpdatedAt,
            FloorKey = candidate.FloorKey,
            LockedTransform = transform,
            LockedGateEvidence =
            [
                new CvAnchorEvidence
                {
                    AnchorId = sideAnchor.Id,
                    Score = Math.Clamp(gate.Score, 0d, 1d),
                    TemplateScale = gate.Scale,
                    ReferenceBounds = referenceBounds,
                    ScreenBounds = gate.ScreenBounds
                }
            ],
            BaselineGateScale = mapScale,
            LastConfidence = priorConfidence,
            LastObservationConfidence = priorConfidence,
            LastSuccessfulAt = DateTimeOffset.UtcNow,
            LastObservationAt = DateTimeOffset.UtcNow,
            HasGatePairLock = false,
            Mode = MapAlignmentTrackingMode.SingleGateTracking,
            SideEntranceScanPriorConfidence = priorConfidence
        };
        return true;
    }

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

        var priorConfidence = Math.Clamp(candidate.MatchScore, 0d, 1d);
        session = new MapAlignmentSession
        {
            MapId = candidate.Map.Id,
            MapUpdatedAt = candidate.Map.UpdatedAt,
            FloorKey = candidate.FloorKey,
            LockedTransform = transform,
            BaselineGateScale = scale,
            LastConfidence = priorConfidence,
            LastObservationConfidence = priorConfidence,
            LastSuccessfulAt = DateTimeOffset.UtcNow,
            LastObservationAt = DateTimeOffset.UtcNow,
            HasGatePairLock = false,
            Mode = MapAlignmentTrackingMode.AuxiliaryAnchorTracking,
            SideEntranceScanPriorConfidence = priorConfidence
        };
        return true;
    }

    /// <summary>允许的最小缩放（识别图 → 实时帧）。</summary>
    public const double MinimumScale = 0.55d;
    /// <summary>允许的最大缩放（识别图 → 实时帧）。</summary>
    public const double MaximumScale = 2.2d;
    // 扫描网格密度（粗步长 / 精化档数）、粗降采样倍率、粗分数剪枝与跨地图
    // 并行度均由 SideEntranceScanRules 提供，三个分辨率预设目录各自通过
    // side_entrance.toml 覆盖（见 Infrastructure/Configuration/Presets）。

    /// <summary>
    /// 对捕获帧执行多尺度模板匹配，返回按得分降序排列的前 topK 候选。
    /// 两阶段搜索：
    /// <list type="number">
    /// <item>粗搜索：在 1/<see cref="SideEntranceScanRules.CoarsePyramidFactor"/>
    /// 降采样帧上对所有候选地图并行遍历缩放网格。降采样帧只计算一次、
    /// 全部分享（原实现每张地图各降采样一次，29 张地图重复 29 次）。</item>
    /// <item>精化：只对粗分前
    /// <see cref="SideEntranceScanRules.RefineCandidateTopK"/> 张地图做全分辨率
    /// 窗口细化，其余直接淘汰——避免 29 张地图全部跑 7 次全分辨率匹配。</item>
    /// </list>
    /// 粗搜索与精化均按 <see cref="SideEntranceScanRules.ScanParallelism"/> 并行。
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

        var valid = candidates
            .Where(c => c.featureTemplate is not null && !c.featureTemplate.Empty())
            .ToList();
        if (valid.Count == 0)
            return [];

        var coarseFactor = Math.Max(2, SideEntranceScanRules.CoarsePyramidFactor);
        var parallelism = Math.Max(1, SideEntranceScanRules.ScanParallelism);
        // 精化上限至少覆盖 topK，默认等于 RefineCandidateTopK。
        var refineTopK = Math.Max(
            topK,
            Math.Min(SideEntranceScanRules.RefineCandidateTopK, valid.Count));

        // 优化1：粗降采样帧只计算一次，全部候选地图共享。
        using var coarseFrame = new Mat();
        Cv2.Resize(
            grayFrame,
            coarseFrame,
            new Size(
                Math.Max(1, grayFrame.Width / coarseFactor),
                Math.Max(1, grayFrame.Height / coarseFactor)),
            0d,
            0d,
            InterpolationFlags.Area);

        // 阶段1：并行粗搜索。每张地图互相独立，各自写独立结果槽位；
        // 并行度受 TOML 约束，避免与 OpenCV 内部线程过订阅。
        var coarseResults = new CoarseResult?[valid.Count];
        Parallel.For(
            0,
            valid.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                var (map, floorKey, template) = valid[i];
                var peak = FindCoarsePeak(coarseFrame, template, coarseFactor);
                coarseResults[i] = peak is { } p
                    ? new CoarseResult(map, floorKey, template, p)
                    : null;
            });

        // 阶段2：粗分数剪枝 —— 低于绝对阈值的地图直接淘汰，且只精化粗分
        // 前 refineTopK 张，其余跳过全分辨率匹配。
        var pruneThreshold = SideEntranceScanRules.CoarseScorePruneThreshold;
        var toRefine = coarseResults
            .Where(r => r is not null && r.Value.Peak.Score >= pruneThreshold)
            .OrderByDescending(r => r!.Value.Peak.Score)
            .Take(refineTopK)
            .Select(r => r!.Value)
            .ToList();

        // 阶段3：并行精化（仅入选地图，全分辨率窗口）。
        var refined = new SideEntranceScanCandidate?[toRefine.Count];
        Parallel.For(
            0,
            toRefine.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                var item = toRefine[i];
                var best = Refine(
                    grayFrame,
                    item.Template,
                    item.Map,
                    item.FloorKey,
                    item.Peak,
                    coarseFactor);
                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Info,
                    $"侧门扫描 {item.Map.SequenceNumber}#{item.FloorKey} · "
                    + $"coarse={item.Peak.Score:P0} · "
                    + $"refined={best?.MatchScore:P0} · scale={best?.MatchScale:F3}");
                refined[i] = best;
            });

        var results = refined.Where(r => r is not null).Select(r => r!).ToList();

        // 按得分降序，取前 topK
        results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
        return results.Count <= topK ? results : results.GetRange(0, topK);
    }

    /// <summary>粗搜索的峰值：缩放档位、降采样图上的匹配左上角与得分。</summary>
    private readonly record struct CoarsePeak(double Scale, int X, int Y, double Score);

    /// <summary>阶段1粗搜索的产出：候选地图 + 其粗峰值（供剪枝与精化使用）。</summary>
    private readonly record struct CoarseResult(
        MapRecord Map,
        string FloorKey,
        Mat Template,
        CoarsePeak Peak);

    /// <summary>
    /// 全分辨率细化：粗扫已把缩放与位置定到降采样图精度（约
    /// ±<see cref="SideEntranceScanRules.CoarsePyramidFactor"/> 像素），精化只需
    /// 在粗峰值周围的一个窗口内、以 1/4 步长补齐相邻缩放档位即可。全帧重搜
    /// 没有额外信息，反而每张地图都要多花 7 次全分辨率匹配的时间。
    /// </summary>
    private static SideEntranceScanCandidate? Refine(
        Mat grayFrame,
        Mat template,
        MapRecord map,
        string floorKey,
        CoarsePeak peak,
        int coarseFactor)
    {
        var refineStep = SideEntranceScanRules.CoarseScaleStep / 4d;
        var maximumScale = peak.Scale
            * (1d + (SideEntranceScanRules.RefineStepsPerSide * refineStep));
        if (!TryBuildRefineWindow(
                grayFrame,
                template,
                peak,
                maximumScale,
                coarseFactor,
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
            peak.Scale);
        for (var index = -SideEntranceScanRules.RefineStepsPerSide;
            index <= SideEntranceScanRules.RefineStepsPerSide;
            index++)
        {
            if (index == 0)
                continue;
            var scale = peak.Scale * (1d + (index * refineStep));
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
    /// <param name="coarseFactor">粗搜索的降采样倍率，决定峰值换算与留边。</param>
    private static bool TryBuildRefineWindow(
        Mat grayFrame,
        Mat template,
        CoarsePeak peak,
        double maximumScale,
        int coarseFactor,
        out Rect window)
    {
        window = default;
        var widest = (int)Math.Round(template.Width * maximumScale);
        var tallest = (int)Math.Round(template.Height * maximumScale);
        // MatchTemplate 要求模板严格小于搜索图，等大只会产出 1×1 的平凡结果。
        if (widest >= grayFrame.Width || tallest >= grayFrame.Height)
            return false;

        // 粗峰值来自 1/coarseFactor 分辨率，本身带有约 ±coarseFactor 像素的
        // 量化误差；再叠加相邻缩放档位造成的峰值漂移，留 4 倍降采样步长。
        var margin = coarseFactor * 4;
        var left = (peak.X * coarseFactor) - margin;
        var top = (peak.Y * coarseFactor) - margin;
        var width = widest + (margin * 2);
        var height = tallest + (margin * 2);
        // 先夹住尺寸，再夹住原点，保证窗口完整落在帧内且仍能容纳模板。
        width = Math.Min(width, grayFrame.Width);
        height = Math.Min(height, grayFrame.Height);
        left = Math.Clamp(left, 0, grayFrame.Width - width);
        top = Math.Clamp(top, 0, grayFrame.Height - height);
        window = new Rect(left, top, width, height);
        return true;
    }

    /// <summary>
    /// 在（已降采样的）粗帧上遍历缩放网格，返回得分最高的那一档缩放及其
    /// 匹配位置。缩放是相对原始分辨率的，位置则是降采样图坐标；降采样只
    /// 影响搜索成本与位置精度，不影响缩放语义。粗帧由调用方一次性构建并
    /// 共享给全部候选地图，避免每张地图重复降采样完整帧。
    /// </summary>
    /// <param name="coarseFrame">1/<paramref name="coarseFactor"/> 分辨率的灰度帧。</param>
    /// <param name="coarseFactor">粗搜索的降采样倍率。</param>
    private static CoarsePeak? FindCoarsePeak(
        Mat coarseFrame,
        Mat template,
        int coarseFactor)
    {
        CoarsePeak? bestPeak = null;
        var bestScore = double.NegativeInfinity;
        for (var scale = MinimumScale;
            scale <= MaximumScale;
            scale *= 1d + SideEntranceScanRules.CoarseScaleStep)
        {
            var width = (int)Math.Round(
                template.Width * scale / coarseFactor);
            var height = (int)Math.Round(
                template.Height * scale / coarseFactor);
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
                bestPeak = new CoarsePeak(scale, maxLoc.X, maxLoc.Y, maxVal);
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
