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
        out MapFeatureCacheKey? repairCacheKey)
    {
        repairCacheKey = null;
        var recoveryTuning = CreateStructureTuningForFloor(
            locked.Map,
            floorKey,
            structureTuning);
        MapOpenAlignmentRouteRules.ApplySteadyScaleRecoveryPolicy(
            recoveryTuning);

        // Start from this floor's neutral geometry. A trusted cache for this
        // exact map/floor/resolution may improve the centre of the broad search;
        // no session, candidate history, calibration ratio, or neighbouring
        // floor scale is eligible for this recovery.
        var recoverySeed = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
            locked.Map,
            floorKey);
        var seedSource = "independent-floor-neutral";
        if (TryGetNoDoorScaleCache(
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
