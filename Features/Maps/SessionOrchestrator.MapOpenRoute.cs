namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignMapOpenWithPreferredRoute(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string targetFloorKey,
        bool isOtherFloor,
        bool recoveringSelectedIdentity,
        MapAlignmentSession alignmentSession,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        Func<bool, MapRecognitionAttempt> fallback,
        out MapFeatureCacheKey? repairCacheKey)
    {
        repairCacheKey = null;

        MapRecognitionAttempt VpsgThenFallback(bool tryDirectSideFeature)
        {
            // 无会话、无缓存时的 scale bootstrap：VPSG 用 AKAZE 描述符几何
            // 独立估算本楼层 scale，成功则短路，失败再进入常规 fallback。
            if (TryAlignFloorWithVpsg(
                    frame,
                    locked,
                    targetFloorKey,
                    alignmentSession.LockedTransform,
                    alignmentMode,
                    tuning,
                    structureTuning,
                    alignmentSession.SideEntranceScanPriorConfidence)
                is { } vpsgAttempt
                && vpsgAttempt.Recognition is not null)
            {
                return vpsgAttempt;
            }
            return fallback(tryDirectSideFeature);
        }

        if (MapOpenAlignmentRouteRules.ShouldPreferLockedSideFeature(
                isOtherFloor,
                recoveringSelectedIdentity,
                alignmentSession.SideEntranceScanPriorConfidence))
        {
            // A same-frame side feature provides a scale and translation
            // proposal. Static structure must validate that proposal before
            // it can be committed; the scale cache remains the fallback.
            var directAttempt =
                _recognition.AlignLockedSideEntranceFeature(
                    frame,
                    locked.Map.Id,
                    alignmentSession,
                    alignmentMode,
                    tuning,
                    structureTuning);
            if (directAttempt.Recognition is not null)
                return directAttempt;

            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "主楼层侧门特征种子未通过结构验证，继续尝试缩放缓存与结构回退",
                details: new()
                {
                    ["route"] = "side-feature-structure-then-scale-cache",
                    ["reason"] = directAttempt.FailureReason
                });
            return AlignUsingScaleCache(
                frame,
                locked.Map,
                targetFloorKey,
                tuning,
                structureTuning,
                alignmentSession.SideEntranceScanPriorConfidence,
                () => VpsgThenFallback(false),
                out repairCacheKey);
        }

        return AlignUsingScaleCache(
            frame,
            locked.Map,
            targetFloorKey,
            tuning,
            structureTuning,
            alignmentSession.SideEntranceScanPriorConfidence,
            () => VpsgThenFallback(true),
            out repairCacheKey);
    }

    private void LogMapOpenAlignmentTimings(
        RuntimeMapRecognition locked,
        string targetFloorKey,
        bool isOtherFloor,
        bool succeeded,
        string? failureReason,
        double wallClockMilliseconds)
    {
        _lastAlignmentPhaseTimings = BuildAlignmentPhaseTimings(
            _lastDiagnostics,
            wallClockMilliseconds);
        var details = _lastAlignmentPhaseTimings.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value);
        details["mapId"] = locked.Map.Id;
        details["floor"] = targetFloorKey;
        details["route"] = isOtherFloor
            ? "structure-only-floor"
            : "primary-floor";
        details["succeeded"] = succeeded;
        details["failureReason"] = failureReason;
        details["targetP50Ms"] =
            MapOpenAlignmentRouteRules.TargetP50Milliseconds;
        details["targetP95Ms"] =
            MapOpenAlignmentRouteRules.TargetP95Milliseconds;
        details["maximumFailureMs"] =
            MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds;
        details["targetReliableRate"] =
            MapOpenAlignmentRouteRules.TargetReliableAlignmentRate;
        details["targetJitterP95Px"] =
            MapOpenAlignmentRouteRules.TargetTranslationJitterP95Pixels;
        _logCollector.Append(
            MapLogCategory.Session,
            succeeded ? MapLogLevel.Info : MapLogLevel.Warning,
            "仅对齐阶段耗时汇总",
            elapsedMs: wallClockMilliseconds,
            details: details);
    }
}
