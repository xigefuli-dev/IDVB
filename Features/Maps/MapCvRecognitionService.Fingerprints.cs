using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private MapGeometryFingerprint? TryCreateFingerprint(MapRecord map)
    {
        map.NormalizeRecognition();
        if (!map.Recognition.HasRequiredIdentificationData())
            return null;
        var floorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? map.Recognition.FirstFloor;
        var main = profile.FindAnchor("main-entrance");
        var side = profile.FindAnchor("side-entrance");
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

        var candidates = new List<(MapRecord map, string floorKey, Mat template)>(
            _sideEntranceFeatureCache.Count);

        foreach (var ((mapId, floorKey), template) in _sideEntranceFeatureCache)
        {
            var map = _maps.FirstOrDefault(m => m.Id == mapId);
            if (map is null
                || (selectedMapId is { } requiredMapId
                    && map.Id != requiredMapId)
                || (!string.IsNullOrWhiteSpace(mapClass)
                    && !string.Equals(
                        map.Class,
                        mapClass,
                        StringComparison.OrdinalIgnoreCase))
                || !string.Equals(
                    floorKey,
                    MapFloorRules.GetPrimaryFloorKey(map),
                    StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add((map, floorKey, template));
        }

        return _sideEntrancePipeline.RunScan(capturedFrame, candidates, topK);
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
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        var gateResult = _gateDetector.Detect(
            liveMatchImage,
            frame.ViewportBounds,
            frame.ClientBounds.Width,
            tuning.GateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold =
                    Math.Max(tuning.GateTemplateThreshold, GateTemplateRules.EarlyExitScoreThreshold),
                SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
                AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap
            });

        var gate = gateResult.Gates
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (gate is null)
        {
            return new SideEntranceScanResult
            {
                GateDetection = gateResult,
                FailureReason =
                    "side-entrance scan requires one visible gate feature; no gate was detected."
            };
        }

        var candidates = RunSideEntranceScan(
            frame.Image,
            topK,
            mapClass);
        return new SideEntranceScanResult
        {
            GateDetection = gateResult,
            Candidates = candidates,
            FailureReason = candidates.Count == 0
                ? "the visible gate was found, but no marked side-entrance feature matched a map."
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

        var confidence = Math.Clamp(
            (candidate.MatchScore * 0.70d) + (gate.Score * 0.30d),
            0d,
            1d);
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
        _auxiliaryTemplateCache.Dispose();
        _cacheGate.Dispose();
        foreach (var mat in _sideEntranceFeatureCache.Values)
            mat.Dispose();
        _sideEntranceFeatureCache = [];
    }
}
