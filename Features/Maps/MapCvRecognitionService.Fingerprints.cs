using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    public bool RequiresSingleFeatureScan(string? mapClass) => _maps
        .Where(map => string.IsNullOrWhiteSpace(mapClass)
            || string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
        .Any(map => !MapScanFloorRules.IsPrimaryFloor(
            map,
            MapScanFloorRules.ResolveScanFloorKey(map)));

    private MapGeometryFingerprint? TryCreateFingerprint(MapRecord map)
    {
        map.NormalizeRecognition();
        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? map.Recognition.FirstFloor;
        var anchors = MapScanFloorRules.GetGeometryAnchors(map, floorKey);
        var main = anchors?.Main;
        var side = anchors?.Side;
        if (main?.Bounds?.IsValid is not true
            || side?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }
        var mainRefBounds = MapCvRecognitionHelpers.ToPixelBounds(
            main.Bounds,
            profile.RecognitionPixelWidth,
            profile.RecognitionPixelHeight);
        var sideRefBounds = MapCvRecognitionHelpers.ToPixelBounds(
            side.Bounds,
            profile.RecognitionPixelWidth,
            profile.RecognitionPixelHeight);
        var recognitionImagePath = _repository.GetFloorRecognitionPath(map, floorKey);

        // Measure actual gate icon size in the reference image so that
        // EstimateAxisScale uses comparable objects on both sides of the
        // ratio (screen-side: template-matched tight box; reference-side:
        // template-matched tight box) instead of comparing a tight box to
        // a user-drawn loose anchor rectangle.
        double iconWidth = 0d;
        double iconHeight = 0d;
        try
        {
            using var reference = Cv2.ImRead(recognitionImagePath, ImreadModes.Unchanged);
            if (!reference.Empty())
            {
                var mainCenter = new Point2d(
                    mainRefBounds.CenterX,
                    mainRefBounds.CenterY);
                var sideCenter = new Point2d(
                    sideRefBounds.CenterX,
                    sideRefBounds.CenterY);
                var mainSize = GateTemplateDetector.EstimateReferenceGateIconSize(
                    reference,
                    mainCenter);
                var sideSize = GateTemplateDetector.EstimateReferenceGateIconSize(
                    reference,
                    sideCenter);
                if (mainSize is { } mainSz && sideSize is { } sideSz)
                {
                    // Average the two measurements — they should be very close.
                    iconWidth = (mainSz.Width + sideSz.Width) / 2d;
                    iconHeight = (mainSz.Height + sideSz.Height) / 2d;
                }
                else if (mainSize is { } mSz)
                {
                    iconWidth = mSz.Width;
                    iconHeight = mSz.Height;
                }
                else if (sideSize is { } sSz)
                {
                    iconWidth = sSz.Width;
                    iconHeight = sSz.Height;
                }
            }
        }
        catch
        {
            // Reference image missing or corrupt — fall back to anchor bounds.
        }

        return new MapGeometryFingerprint
        {
            Map = map,
            FloorKey = floorKey,
            MainPoint = MapCvRecognitionHelpers.Center(main.Bounds),
            SidePoint = MapCvRecognitionHelpers.Center(side.Bounds),
            MainReferenceBounds = mainRefBounds,
            SideReferenceBounds = sideRefBounds,
            ReferenceWidth = profile.RecognitionPixelWidth,
            ReferenceHeight = profile.RecognitionPixelHeight,
            RecognitionImagePath = recognitionImagePath,
            OverlayImagePath = _repository.GetFloorOverlayPath(map, floorKey),
            ReferenceGateIconWidth = iconWidth,
            ReferenceGateIconHeight = iconHeight
        };
    }

    // ── 侧门特征缓存与扫描 ────────────────────────────────────────────

    /// <summary>
    /// 使用侧门特征缓存对捕获帧执行模板匹配，返回 top-<paramref name="topK"/> 候选。
    /// </summary>
    public IReadOnlyList<SideEntranceScanCandidate> RunSideEntranceScan(
        Mat capturedFrame,
        int topK = 5,
        string? mapClass = null,
        Guid? selectedMapId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (capturedFrame.Empty() || _sideEntranceFeatureCache.Count == 0)
            return [];

        var candidates = BuildSideEntranceScanInputs(mapClass, selectedMapId);
        return _sideEntrancePipeline.RunScan(capturedFrame, candidates, topK);
    }

    private List<(MapRecord map, string floorKey, Mat template)>
        BuildSideEntranceScanInputs(string? mapClass, Guid? selectedMapId = null)
    {
        var candidates = new List<(MapRecord map, string floorKey, Mat template)>(
            _sideEntranceFeatureCache.Count);
        foreach (var ((mapId, floorKey), template) in _sideEntranceFeatureCache)
        {
            var map = _maps.FirstOrDefault(item => item.Id == mapId);
            if (map is null)
                continue;

            var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
            if ((selectedMapId is { } requiredMapId && map.Id != requiredMapId)
                || (!string.IsNullOrWhiteSpace(mapClass)
                    && !string.Equals(map.Class, mapClass,
                        StringComparison.OrdinalIgnoreCase))
                || !string.Equals(floorKey, scanFloorKey,
                    StringComparison.OrdinalIgnoreCase)
                || !MapScanFloorRules.HasRequiredScanMarkers(map, floorKey))
            {
                continue;
            }
            candidates.Add((map, floorKey, template));
        }
        return candidates;
    }

    /// <summary>
    /// Runs the side-entrance identity scan with the mandatory gate evidence.
    /// The user-authored side-entrance feature remains the map discriminator,
    /// but it can no longer identify a map when the live frame contains no
    /// detectable gate.
    /// </summary>
    public SideEntranceScanResult RunSideEntranceScan(
        CapturedGameFrame frame,
        MapRecognitionTuning tuning,
        int topK = 5,
        string? mapClass = null,
        Action<double>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        progress?.Invoke(0d);
        using var livePreprocess = MapOperationTraceAmbient.StartChild(
            "side_scan_live_preprocess",
            MapOperationWaitKind.Compute);
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        livePreprocess.Complete();
        using var gateDetection = MapOperationTraceAmbient.StartChild(
            "side_gate_detection",
            MapOperationWaitKind.Compute);
        var gateResult = _gateDetector.Detect(
            liveMatchImage,
            frame.ViewportBounds,
            frame.ClientBounds.Width,
            tuning.GateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
                AllowDualGateEarlyExit = false,
                AllowSingleGateEarlyExit = false,
                SingleGateScoreThreshold =
                    Math.Max(tuning.GateTemplateThreshold, GateTemplateRules.EarlyExitScoreThreshold),
                SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
                AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap
            });
        gateDetection.Complete();
        progress?.Invoke(0.12d);

        if (gateResult.Gates.Count == 0)
        {
            return new SideEntranceScanResult
            {
                GateDetection = gateResult,
                FailureReason =
                    "side-entrance scan requires one visible gate feature; no gate was detected."
            };
        }

        using var repositoryRead = MapOperationTraceAmbient.StartChild(
            "side_map_repository_read",
            MapOperationWaitKind.Io);
        var inputs = BuildSideEntranceScanInputs(mapClass);
        repositoryRead.Complete();
        var eligibleMapCount = _maps.Count(map =>
            (string.IsNullOrWhiteSpace(mapClass)
                || string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            && MapScanFloorRules.GetScanFeatureAnchor(
                map,
                MapScanFloorRules.ResolveScanFloorKey(map))?.IsMarked is true);
        var candidates = _sideEntrancePipeline.RunScan(
            frame.Image,
            inputs,
            gateResult.Gates,
            Math.Max(topK, inputs.Count),
            frame.ViewportBounds,
            progress: value => progress?.Invoke(0.12d + value * 0.88d));
        return new SideEntranceScanResult
        {
            GateDetection = gateResult,
            Candidates = candidates,
            EligibleMapCount = eligibleMapCount,
            ReadyMapCount = inputs.Count,
            RejectedCandidateCount = Math.Max(0, inputs.Count - candidates.Count),
            FailureReason = candidates.Count == 0
                ? inputs.Count == 0
                    ? $"当前地图类别没有可用的侧门特征（就绪 {inputs.Count}/{eligibleMapCount}）。"
                    : "检测到侧门，但没有地图通过最低证据门槛。"
                : string.Empty
        };
    }

    /// <summary>
    /// Builds the provisional selected-map result used by the candidate UI.
    /// It is scan evidence only; the caller must run AlignSideEntrance after
    /// the user confirms the map.
    /// </summary>
    public bool TryCreateSideEntranceSelection(
        SideEntranceScanCandidate candidate,
        MapScreenRect viewportBounds,
        out RuntimeMapRecognition recognition,
        out MapAlignmentSession session,
        out string failureReason)
    {
        recognition = new RuntimeMapRecognition();
        if (!TryCreateSideEntranceAlignmentSeed(
                candidate,
                viewportBounds,
                out session,
                out failureReason))
        {
            return false;
        }

        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == candidate.Map.Id
            && string.Equals(item.FloorKey, candidate.FloorKey, StringComparison.Ordinal));
        if (fingerprint is null)
        {
            failureReason = "the selected side-entrance candidate is no longer in the map cache.";
            return false;
        }

        var confidence = candidate.Disposition ==
            SideEntranceCandidateDisposition.Reliable
                ? Math.Clamp(candidate.IdentityConfidence, 0d, 1d)
                : 0d;
        recognition = MapCvRecognitionBuilders.BuildTrackedRecognition(
            fingerprint,
            session.LockedTransform,
            session.LockedGateEvidence,
            MapRecognitionSource.SideEntranceSelection,
            confidenceOverride: confidence,
            evidenceKind: MapAlignmentEvidenceKind.None);
        return true;
    }

    public bool TryCreateSideEntranceSelection(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewportBounds,
        out RuntimeMapRecognition recognition,
        out MapAlignmentSession session,
        out string failureReason)
    {
        recognition = new RuntimeMapRecognition();
        if (!TryCreateSideEntranceAlignmentSeed(
                candidate,
                gate,
                viewportBounds,
                out session,
                out failureReason))
        {
            return false;
        }

        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == candidate.Map.Id
            && string.Equals(item.FloorKey, candidate.FloorKey, StringComparison.Ordinal));
        if (fingerprint is null)
        {
            failureReason = "the selected side-entrance candidate is no longer in the map cache.";
            return false;
        }

        // This is a provisional identity used to carry a user selection into
        // strict selected-map alignment. Template similarity and a shared gate
        // detection are not calibrated identity confidence.
        var confidence = candidate.Disposition ==
            SideEntranceCandidateDisposition.Reliable
                ? Math.Clamp(candidate.IdentityConfidence, 0d, 1d)
                : 0d;
        recognition = MapCvRecognitionBuilders.BuildTrackedRecognition(
            fingerprint,
            session.LockedTransform,
            session.LockedGateEvidence,
            MapRecognitionSource.SideEntranceSelection,
            confidenceOverride: confidence,
            evidenceKind: MapAlignmentEvidenceKind.None);
        return true;
    }

    public bool TryCreateSideEntranceAlignmentSeed(
        SideEntranceScanCandidate candidate,
        MapScreenRect viewportBounds,
        out MapAlignmentSession session,
        out string failureReason)
    {
        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == candidate.Map.Id
            && string.Equals(item.FloorKey, candidate.FloorKey, StringComparison.Ordinal));
        if (fingerprint is null)
        {
            session = new MapAlignmentSession();
            failureReason = "the selected side-entrance candidate is no longer in the map cache.";
            return false;
        }

        if (candidate.AssociatedGate is { } gate)
        {
            return SideEntranceScanPipeline.TryCreateGateAlignmentSeed(
                candidate,
                gate,
                viewportBounds,
                fingerprint.ReferenceGateIconWidth,
                fingerprint.ReferenceGateIconHeight,
                out session,
                out failureReason);
        }

        return SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            viewportBounds,
            out session,
            out failureReason);
    }

    public bool TryCreateSideEntranceAlignmentSeed(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewportBounds,
        out MapAlignmentSession session,
        out string failureReason)
    {
        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == candidate.Map.Id
            && string.Equals(item.FloorKey, candidate.FloorKey, StringComparison.Ordinal));
        if (fingerprint is null)
        {
            session = new MapAlignmentSession();
            failureReason = "the selected side-entrance candidate is no longer in the map cache.";
            return false;
        }

        return SideEntranceScanPipeline.TryCreateGateAlignmentSeed(
            candidate,
            gate,
            viewportBounds,
            fingerprint.ReferenceGateIconWidth,
            fingerprint.ReferenceGateIconHeight,
            out session,
            out failureReason);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gateDetector.Dispose();
        _structureCache.Dispose();
        DisposeVpsg3();
        _auxiliaryTemplateCache.Dispose();
        _cacheGate.Dispose();
        foreach (var mat in _sideEntranceFeatureCache.Values)
            mat.Dispose();
        _sideEntranceFeatureCache = [];
    }
}
/*
 * 文件职责：MapCvRecognitionService.Fingerprints。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
