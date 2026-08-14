using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private bool TryGetAdaptiveScaleSeed(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        out AdaptiveScaleSeedDecision? seed)
    {
        seed = null;
        if (!_adaptiveScale.Enabled)
            return false;
        var key = AdaptiveScaleKey.Create(
            map,
            floorKey,
            frame.ClientBounds,
            frame.ViewportBounds);
        return _adaptiveScale.TryGetPreferredSeed(
            key,
            _gameMapToggleState.Version,
            out seed);
    }

    private bool TryAlignWithAdaptiveCalibrationSeed(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        out AdaptiveScaleSeedDecision? seed,
        out MapRecognitionAttempt? attempt)
    {
        seed = null;
        attempt = null;
        var key = AdaptiveScaleKey.Create(
            map,
            floorKey,
            frame.ClientBounds,
            frame.ViewportBounds);
        if (!TryGetAdaptiveScaleSeed(frame, map, floorKey, out seed)
            || seed is null)
        {
            return false;
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        attempt = _recognition.AlignWithCachedScale(
            frame,
            map.Id,
            floorKey,
            MapFeatureCacheRules.CreateScaleSeed(map, floorKey, seed.Scale),
            alignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence);
        timer.Stop();
        attempt.Diagnostics.ScaleSeedSource = seed.Source == AdaptiveScaleSeedSource.Runtime
            ? "adaptive-runtime"
            : "adaptive-calibration";
        attempt.Diagnostics.ScaleSeedScale = seed.Scale;
        attempt.Diagnostics.ScaleSeedTargetViewportWidth = key.ViewportWidth;
        attempt.Diagnostics.ScaleSeedTargetViewportHeight = key.ViewportHeight;
        attempt.Diagnostics.FinalValidatedScale =
            attempt.Recognition?.Result.OverlayTransform?.ScaleX ?? 0d;
        var qualityAccepted = IsAdaptiveInitialScaleQualified(
            attempt,
            structureTuning);
        if (!qualityAccepted)
        {
            attempt.Diagnostics.ScaleSeedRejectionReason =
                attempt.Recognition is not null
                    ? "adaptive-initial-quality-gate"
                    : string.IsNullOrWhiteSpace(attempt.StructureFailureReason)
                        ? attempt.FailureReason ?? "fixed-scale-validation-failed"
                        : attempt.StructureFailureReason;
        }

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            qualityAccepted ? MapLogLevel.Info : MapLogLevel.Warning,
            "自适应标定 seed 当前帧结构验证完成",
            elapsedMs: timer.Elapsed.TotalMilliseconds,
            details: new Dictionary<string, object?>
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["source"] = seed.Source.ToString(),
                ["scale"] = seed.Scale,
                ["client"] = $"{key.ClientWidth}x{key.ClientHeight}",
                ["viewport"] = $"{key.ViewportWidth}x{key.ViewportHeight}",
                ["accepted"] = qualityAccepted,
                ["failure"] = attempt.Diagnostics.ScaleSeedRejectionReason
            });
        return true;
    }
}
