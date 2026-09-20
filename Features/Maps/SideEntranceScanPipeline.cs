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

        var scanAnchor = MapScanFloorRules.GetScanFeatureAnchor(
            candidate.Map,
            candidate.FloorKey);
        if (scanAnchor?.Bounds?.IsValid is not true)
        {
            failureReason = "the selected map has no marked scan gate feature.";
            return false;
        }

        var referenceBounds = new MapScreenRect(
            scanAnchor.Bounds.X * profile.RecognitionPixelWidth,
            scanAnchor.Bounds.Y * profile.RecognitionPixelHeight,
            scanAnchor.Bounds.Width * profile.RecognitionPixelWidth,
            scanAnchor.Bounds.Height * profile.RecognitionPixelHeight);

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
                    AnchorId = scanAnchor.Id,
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
    /// Creates a provisional alignment session from the configured scan-gate feature
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
            failureReason = "扫描门特征匹配位置或当前地图视口无效。";
            return false;
        }

        var profile = MapFloorRules.GetFloorProfile(
            candidate.Map,
            candidate.FloorKey);
        if (profile is null
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            failureReason = "扫描门特征对应楼层缺少有效的识别图尺寸。";
            return false;
        }

        var referenceCenterX = profile.SideEntranceFeatureCenterX;
        var referenceCenterY = profile.SideEntranceFeatureCenterY;
        if (!double.IsFinite(referenceCenterX)
            || !double.IsFinite(referenceCenterY)
            || referenceCenterX <= 0d
            || referenceCenterY <= 0d)
        {
            var anchor = MapScanFloorRules.GetScanFeatureAnchor(
                candidate.Map,
                candidate.FloorKey);
            if (anchor?.Bounds?.IsValid is not true)
            {
                failureReason = "扫描门特征缺少可用的参考中心点。";
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
            failureReason = "扫描门特征无法生成有效的初始缩放。";
            return false;
        }

        var screenCenterX = viewportBounds.X + candidate.MatchLocation.CenterX;
        var screenCenterY = viewportBounds.Y + candidate.MatchLocation.CenterY;
        var offsetX = screenCenterX - (referenceCenterX * scale);
        var offsetY = screenCenterY - (referenceCenterY * scale);
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            failureReason = "扫描门特征无法生成有效的初始位移。";
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
}
/*
 * 文件职责：SideEntranceScanPipeline。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
