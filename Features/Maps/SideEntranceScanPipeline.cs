using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 侧门专属扫描管线：对捕获帧运行模板匹配，返回 TopK 候选地图。
/// 与双门管线并列，仅用于首次地图识别，对齐阶段仍由原有管线处理。
/// </summary>
public sealed partial class SideEntranceScanPipeline
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
            || mapScale < SideEntranceScanRules.MinimumScale
            || mapScale > SideEntranceScanRules.MaximumScale)
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
            || scale < SideEntranceScanRules.MinimumScale
            || scale > SideEntranceScanRules.MaximumScale)
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

    // 侧门扫描缩放边界已下沉到 SideEntranceScanRules（side_entrance.toml 的
    // [side_entrance] 段 minimum_scale / maximum_scale），按分辨率预设可单独配置。
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
    /// <item>精化：对所有通过粗分绝对下限的地图做全分辨率窗口细化；
    /// topK 只限制最后交给调用方的线索数量，不参与身份召回剪枝。</item>
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
        int topK = 5,
        GateDetection? detectedGate = null,
        MapScreenRect? viewportBounds = null,
        Action<double>? progress = null) =>
        RunSingleGateScan(
            capturedFrame,
            candidates,
            topK,
            detectedGate,
            viewportBounds,
            maskDetectedGate: true,
            gateIndexForDiagnostics: null,
            progress: progress);

    private IReadOnlyList<SideEntranceScanCandidate> RunSingleGateScan(
        Mat capturedFrame,
        IReadOnlyList<(MapRecord map, string floorKey, Mat featureTemplate)> candidates,
        int topK,
        GateDetection? detectedGate,
        MapScreenRect? viewportBounds,
        bool maskDetectedGate,
        int? gateIndexForDiagnostics,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(capturedFrame);
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK < 1)
            topK = 1;
        if (capturedFrame.Empty() || candidates.Count == 0)
            return [];

        using var grayscale = MapOperationTraceAmbient.StartChild(
            "side_scan_grayscale",
            MapOperationWaitKind.Compute);
        // 将捕获帧转为灰度，供模板匹配使用
        using var grayFrame = new Mat();
        if (capturedFrame.Channels() == 1)
            capturedFrame.CopyTo(grayFrame);
        else
            Cv2.CvtColor(capturedFrame, grayFrame, ColorConversionCodes.BGR2GRAY);
        grayscale.Complete();

        // Remove the detected common gate glyph from the live image, matching
        // the persisted v2 feature preprocessing. This prevents one shared UI
        // icon from acting as map identity evidence.
        if (maskDetectedGate
            && detectedGate is not null
            && viewportBounds is { IsValid: true } viewport
            && detectedGate.ScreenBounds.IsValid)
        {
            var local = new Rect(
                (int)Math.Floor(detectedGate.ScreenBounds.X - viewport.X),
                (int)Math.Floor(detectedGate.ScreenBounds.Y - viewport.Y),
                (int)Math.Ceiling(detectedGate.ScreenBounds.Width),
                (int)Math.Ceiling(detectedGate.ScreenBounds.Height));
            local = local.Intersect(new Rect(0, 0, grayFrame.Width, grayFrame.Height));
            if (local.Width > 0 && local.Height > 0)
            {
                var mean = Cv2.Mean(grayFrame);
                Cv2.Rectangle(grayFrame, local, new Scalar(mean.Val0), -1);
            }
        }

        var valid = candidates
            .Where(c => c.featureTemplate is not null && !c.featureTemplate.Empty())
            .ToList();
        if (valid.Count == 0)
            return [];

        // Search only where a feature crop could geometrically contain the
        // detected gate. The generous radius covers edge-clamped feature
        // centers at every supported map scale, while excluding unrelated
        // repeated corridors elsewhere in the viewport.
        var searchBounds = new Rect(0, 0, grayFrame.Width, grayFrame.Height);
        if (detectedGate is not null
            && viewportBounds is { IsValid: true } gateViewport)
        {
            var gateX = detectedGate.ScreenBounds.CenterX - gateViewport.X;
            var gateY = detectedGate.ScreenBounds.CenterY - gateViewport.Y;
            var largestTemplateExtent = valid.Max(item =>
                Math.Max(item.featureTemplate.Width, item.featureTemplate.Height));
            var radius = (int)Math.Ceiling(
                (largestTemplateExtent * SideEntranceScanRules.MaximumScale)
                + (SideEntranceScanRules.MaximumGateSpatialResidualPixels * 2d));
            var left = Math.Clamp((int)Math.Floor(gateX) - radius,
                0, Math.Max(0, grayFrame.Width - 1));
            var top = Math.Clamp((int)Math.Floor(gateY) - radius,
                0, Math.Max(0, grayFrame.Height - 1));
            var right = Math.Clamp((int)Math.Ceiling(gateX) + radius,
                left + 1, grayFrame.Width);
            var bottom = Math.Clamp((int)Math.Ceiling(gateY) + radius,
                top + 1, grayFrame.Height);
            searchBounds = new Rect(left, top, right - left, bottom - top);
        }

        using var gateConstrainedFrame = new Mat(grayFrame, searchBounds);

        var coarseFactor = Math.Max(2, SideEntranceScanRules.CoarsePyramidFactor);
        var parallelism = Math.Max(1, SideEntranceScanRules.ScanParallelism);
        // 身份识别不能因为性能剪枝永久丢失正确地图。准确优先模式下
        // 对全部粗搜索结果做全分辨率精化，topK 只限制最终展示数量。
        var refineTopK = valid.Count;

        // 优化1：粗降采样帧只计算一次，全部候选地图共享。
        using var coarseFrame = new Mat();
        Cv2.Resize(
            gateConstrainedFrame,
            coarseFrame,
            new Size(
                Math.Max(1, gateConstrainedFrame.Width / coarseFactor),
                Math.Max(1, gateConstrainedFrame.Height / coarseFactor)),
            0d,
            0d,
            InterpolationFlags.Area);

        // 阶段1：并行粗搜索。每张地图互相独立，各自写独立结果槽位；
        // 并行度受 TOML 约束，避免与 OpenCV 内部线程过订阅。
        var coarseResults = new CoarseResult?[valid.Count];
        var coarseCompleted = 0;
        using var coarseSearch = MapOperationTraceAmbient.StartChild(
            "side_map_coarse_search",
            MapOperationWaitKind.Compute);
        Parallel.For(
            0,
            valid.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                var (map, floorKey, template) = valid[i];
                using var mapCoarse = MapOperationTraceAmbient.StartChild(
                    "map_coarse_search",
                    MapOperationWaitKind.Compute,
                    mapId: map.Id.ToString("D"),
                    floorKey: floorKey,
                    attemptIndex: i);
                var peak = FindCoarsePeak(
                    coarseFrame, template, coarseFactor,
                    $"{map.SequenceNumber}#{floorKey}");
                coarseResults[i] = peak is { } p
                    ? new CoarseResult(map, floorKey, template, p)
                    : null;
                progress?.Invoke(0.7d * Interlocked.Increment(ref coarseCompleted) / valid.Count);
            });
        coarseSearch.Complete();

        // 阶段2：仅应用粗分绝对下限；准确优先模式不按排名截断召回。
        var pruneThreshold = SideEntranceScanRules.CoarseScorePruneThreshold;
        var toRefine = coarseResults
            .Where(r => r is not null && r.Value.Peak.Score >= pruneThreshold)
            .OrderByDescending(r => r!.Value.Peak.Score)
            .Take(refineTopK)
            .Select(r => r!.Value)
            .ToList();

        // 阶段3：并行精化（仅入选地图，全分辨率窗口）。
        var refined = new SideEntranceScanCandidate?[toRefine.Count];
        var refineCompleted = 0;
        using var refinement = MapOperationTraceAmbient.StartChild(
            "side_map_refinement",
            MapOperationWaitKind.Compute);
        Parallel.For(
            0,
            toRefine.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                var item = toRefine[i];
                using var mapRefinement = MapOperationTraceAmbient.StartChild(
                    "map_refinement",
                    MapOperationWaitKind.Compute,
                    mapId: item.Map.Id.ToString("D"),
                    floorKey: item.FloorKey,
                    attemptIndex: i);
                var best = Refine(
                    gateConstrainedFrame,
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
                progress?.Invoke(0.7d + 0.3d * Interlocked.Increment(ref refineCompleted) / Math.Max(1, toRefine.Count));
            });
        refinement.Complete();

        var results = refined.Where(r => r is not null).Select(r => r!).ToList();
        if (searchBounds.X != 0 || searchBounds.Y != 0)
        {
            foreach (var candidate in results)
            {
                candidate.MatchLocation = new MapScreenRect(
                    candidate.MatchLocation.X + searchBounds.X,
                    candidate.MatchLocation.Y + searchBounds.Y,
                    candidate.MatchLocation.Width,
                    candidate.MatchLocation.Height);
            }
        }

        // 模板分数只负责提出线索。绝对分、分离度、门空间关系和缩放
        // 边界均是硬性证据检查；不合格项不得拿来填满 Top-K。
        using var finalRanking = MapOperationTraceAmbient.StartChild(
            "side_candidate_final_ranking",
            MapOperationWaitKind.Compute);
        results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
        for (var index = 0; index < results.Count; index++)
        {
            var candidate = results[index];
            var previousGap = index > 0
                ? results[index - 1].MatchScore - candidate.MatchScore
                : double.PositiveInfinity;
            var nextGap = index + 1 < results.Count
                ? candidate.MatchScore - results[index + 1].MatchScore
                : double.PositiveInfinity;
            candidate.TemplateMargin = results.Count == 1
                ? candidate.MatchScore
                : Math.Min(previousGap, nextGap);
            ClassifyTemplateEvidence(candidate, detectedGate, viewportBounds);
            if (candidate.Disposition == SideEntranceCandidateDisposition.Rejected)
            {
                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Warning,
                    $"侧门线索已拒绝 · map={candidate.Map.SequenceNumber}#{candidate.FloorKey} "
                    + $"· reason={candidate.RejectionReason} · {candidate.RejectionDetail}",
                    details: new()
                    {
                        ["mapId"] = candidate.Map.Id,
                        ["templateSimilarity"] = candidate.MatchScore,
                        ["templateMargin"] = candidate.TemplateMargin,
                        ["gateSpatialResidualPixels"] =
                            candidate.GateSpatialResidualPixels,
                        ["matchScale"] = candidate.MatchScale,
                        ["rejectionReason"] = candidate.RejectionReason.ToString(),
                        ["gateIndex"] = gateIndexForDiagnostics,
                        ["gateScore"] = detectedGate?.Score,
                        ["gateScale"] = detectedGate?.Scale,
                        ["gateBounds"] = detectedGate is null
                            ? null
                            : $"{detectedGate.ScreenBounds.X:F1},"
                                + $"{detectedGate.ScreenBounds.Y:F1},"
                                + $"{detectedGate.ScreenBounds.Width:F1},"
                                + $"{detectedGate.ScreenBounds.Height:F1}"
                    });
            }
        }

        var eligible = results
            .Where(candidate => candidate.Disposition !=
                SideEntranceCandidateDisposition.Rejected)
            .Take(topK)
            .ToList();
        return eligible;
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
            if (scale < SideEntranceScanRules.MinimumScale
                || scale > SideEntranceScanRules.MaximumScale)
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
        int coarseFactor,
        string logContext)
    {
        CoarsePeak? bestPeak = null;
        var bestScore = double.NegativeInfinity;
        var response = new List<(double Scale, double Score)>();
        for (var scale = SideEntranceScanRules.MinimumScale;
            scale <= SideEntranceScanRules.MaximumScale;
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
            if (double.IsFinite(maxVal))
                response.Add((scale, maxVal));
            if (double.IsFinite(maxVal) && maxVal > bestScore)
            {
                bestScore = maxVal;
                bestPeak = new CoarsePeak(scale, maxLoc.X, maxLoc.Y, maxVal);
            }
        }

        if (response.Count > 0)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"侧门粗搜索尺度响应 {logContext}",
                details: new()
                {
                    ["scales"] = string.Join(
                        ",", response.Select(r => r.Scale.ToString("F3"))),
                    ["scores"] = string.Join(
                        ",", response.Select(r => r.Score.ToString("F4")))
                });
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
/*
 * 文件职责：SideEntranceScanPipeline。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
