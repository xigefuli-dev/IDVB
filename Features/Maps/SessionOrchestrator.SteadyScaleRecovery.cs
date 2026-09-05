namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignSteadyScaleRecovery(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        MapRecognitionAttempt fixedScaleFailure,
        out MapFeatureCacheKey? repairCacheKey,
        ReliableFloorAlignmentSeed? warmSeed = null)
    {
        repairCacheKey = null;

        double? knownScale = null;
        if (warmSeed is not null
            && string.Equals(warmSeed.Session.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase)
            && warmSeed.Session.LockedTransform.ScaleX > 0.1d)
        {
            knownScale = warmSeed.Session.LockedTransform.ScaleX;
        }
        else if (TryGetNoDoorScaleCache(
                frame,
                locked.Map,
                floorKey,
                out var scaleCacheKey,
                out var scaleCacheEntry)
            && scaleCacheKey is not null
            && scaleCacheEntry is not null
            && MapFeatureCacheRules.IsCacheEntryTrusted(scaleCacheEntry))
        {
            knownScale = scaleCacheEntry.Scale.UniformScale;
        }

        // 稳态恢复优先尝试极速 VPSG 3.0：直接求解真实尺度与平移，将耗时从 >500ms 降低至 ~30ms
        if (_recognition.TryAlignWithVpsg3(
                frame,
                locked.Map,
                floorKey,
                identityPriorConfidence,
                out var vpsg3Attempt,
                knownScaleSeed: knownScale))
        {
            AccumulateNoDoorStageTimings(
                fixedScaleFailure.Diagnostics,
                vpsg3Attempt.Diagnostics);
            LogNoDoorStage(
                "steady-scale-recovery-vpsg3",
                vpsg3Attempt.Recognition is not null,
                vpsg3Attempt,
                vpsg3Attempt.Diagnostics.TotalMilliseconds,
                new Dictionary<string, object?>
                {
                    ["floor"] = floorKey,
                    ["scale"] = vpsg3Attempt.Diagnostics.ScaleBootstrapScale,
                    ["method"] = "vpsg3",
                    ["elapsedMs"] = vpsg3Attempt.Diagnostics.TotalMilliseconds
                });
            return vpsg3Attempt;
        }

        var recoveryTuning = CreateStructureTuningForFloor(
            locked.Map,
            floorKey,
            structureTuning);
        MapOpenAlignmentRouteRules.ApplySteadyScaleRecoveryPolicy(
            recoveryTuning);

        // 尺度恢复约束：只允许使用目标楼层自身的可靠会话或缓存；若不存在，才从中性种子独立估算。
        MapOverlayTransform recoverySeed;
        string seedSource;
        if (warmSeed is not null
            && string.Equals(warmSeed.Session.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase)
            && warmSeed.Session.LockedTransform.ScaleX > 0.1d)
        {
            recoverySeed = MapFeatureCacheRules.CreateScaleSeed(
                locked.Map,
                floorKey,
                warmSeed.Session.LockedTransform.ScaleX);
            seedSource = "same-floor-steady-session";
        }
        else if (TryGetNoDoorScaleCache(
                frame,
                locked.Map,
                floorKey,
                out var cacheKey,
                out var cacheEntry)
            && cacheKey is not null
            && cacheEntry is not null
            && MapFeatureCacheRules.IsCacheEntryTrusted(cacheEntry))
        {
            recoverySeed = MapFeatureCacheRules.CreateScaleSeed(
                locked.Map,
                floorKey,
                cacheEntry.Scale.UniformScale);
            seedSource = "trusted-same-floor-cache";
            repairCacheKey = cacheKey;
        }
        else
        {
            recoverySeed = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                locked.Map,
                floorKey);
            seedSource = "independent-floor-neutral";
        }

        var recovery = _recognition.AlignFloorWithoutGates(
            frame,
            locked.Map.Id,
            floorKey,
            recoverySeed,
            alignmentMode,
            tuning,
            recoveryTuning,
            candidateHistory: null,
            isTracking: false,
            scaleSearchPolicy: MapScaleSearchPolicy.Search,
            identityPriorConfidence: identityPriorConfidence,
            allowPrimaryFloor: true);
        AccumulateNoDoorStageTimings(
            fixedScaleFailure.Diagnostics,
            recovery.Diagnostics);
        LogNoDoorStage(
            "steady-scale-recovery",
            recovery.Recognition is not null,
            recovery,
            recovery.Diagnostics.TotalMilliseconds,
            new Dictionary<string, object?>
            {
                ["floor"] = floorKey,
                ["fixedScaleRejection"] = fixedScaleFailure.StructureResult?
                    .RejectionReason.ToString(),
                ["seedSource"] = seedSource,
                ["seedScale"] = recoverySeed.ScaleX,
                ["scaleRadius"] = MapOpenAlignmentRouteRules
                    .SteadyScaleRecoverySearchRadius,
                ["scaleSearchPolicy"] = nameof(MapScaleSearchPolicy.Search),
                ["translationSearch"] = "unrestricted",
                ["candidateHistoryCount"] = 0
            });
        return recovery;
    }
}
