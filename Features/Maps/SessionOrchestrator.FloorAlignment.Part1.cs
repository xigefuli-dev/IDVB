using IDVBuff.Pipeline;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

    private MapRecognitionAttempt AlignLowStructureFloor(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        out MapFeatureCacheKey? repairCacheKey)
    {
        repairCacheKey = null;
        var config = LowStructureAlignmentPlan.CreateConfig(structureTuning);
        var match = _matchSession.Snapshot;
        var wallClock = Stopwatch.StartNew();
        var cacheTrustLevel = string.Empty;

        MapRecognitionAttempt StampCacheTrust(MapRecognitionAttempt attempt)
        {
            attempt.Diagnostics.LowStructureCacheTrustLevel = cacheTrustLevel;
            return attempt;
        }

        MapRecognitionAttempt RunFixed(
            double scale,
            bool restrictTranslation) =>
            _recognition.AlignWithCachedScale(
                frame,
                locked.Map.Id,
                floorKey,
                MapFeatureCacheRules.CreateScaleSeed(
                    locked.Map,
                    floorKey,
                    scale),
                alignmentMode,
                tuning,
                structureTuning,
                identityPriorConfidence,
                restrictTranslation,
                LowStructureAlignmentPlan.CachedFixed(scale, config));

        if (TryGetManualFloorScaleLock(
                match,
                frame,
                locked.Map,
                floorKey,
                out var manualScale))
        {
            var local = RunFixed(manualScale, true);
            if (local.Recognition is not null)
                return StampCacheTrust(local);
            var global = RunFixed(manualScale, false);
            if (global.Recognition is not null)
                return StampCacheTrust(global);
            return StampCacheTrust(global);
        }

        var lowSeed = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
            locked.Map,
            floorKey);
        var lowResolution = GetResolution(frame);
        var lowCacheKey = lowResolution.IsSupported
            ? CreateAlignmentCacheKey(
                locked.Map,
                floorKey,
                lowResolution,
                structureTuning)
            : null;
        MapFeatureCacheEntry? cacheEntry = null;
        double? cachedScaleForGlobalFallback = null;
        MapFeatureCacheKey? cachedKeyForGlobalFallback = null;

        MapRecognitionAttempt ResolveAfterIndependentSearchFailure(
            MapRecognitionAttempt searchFailure)
        {
            if (cachedScaleForGlobalFallback is not { } cachedScale
                || cachedKeyForGlobalFallback is not { } cachedKey)
            {
                return searchFailure;
            }

            // A global-translation match at the same fixed scale is only a
            // display fallback. Local validation and an independent bounded
            // scale search have both failed, so it must degrade cache trust
            // and must never certify or lock that scale.
            var global = RunFixed(cachedScale, false);
            NoteCacheValidationOutcome(cachedKey, succeeded: false);
            MarkMapCacheForRepair(cachedKey);
            if (global.Recognition is null)
                return searchFailure;
            return StampCacheTrust(CopyAttempt(
                global,
                MarkUsedCachedScale(global.Recognition)));
        }

        if (lowCacheKey is not null
            && _mapFeatureCacheRepository.TryGet(
                lowCacheKey,
                out var foundCacheEntry)
            && foundCacheEntry is not null)
        {
            cacheEntry = foundCacheEntry;
            cacheTrustLevel = cacheEntry.Scale.Validation?
                .LowStructureTrustLevel.ToString() ?? string.Empty;
            if (!MapFeatureCacheRules.IsCacheEntryTrusted(cacheEntry))
            {
                repairCacheKey = lowCacheKey;
                MarkMapCacheForRepair(lowCacheKey);
            }
            else
            {
                var cachedScale = cacheEntry.Scale.UniformScale;
                var local = RunFixed(cachedScale, true);
                if (local.Recognition is not null)
                {
                    return StampCacheTrust(CopyAttempt(
                        local,
                        MarkUsedCachedScale(local.Recognition)));
                }

                cachedScaleForGlobalFallback = cachedScale;
                cachedKeyForGlobalFallback = lowCacheKey;
                repairCacheKey = lowCacheKey;
                MarkMapCacheForRepair(lowCacheKey);
            }
        }

        if (wallClock.ElapsedMilliseconds >= config.EndToEndBudgetMilliseconds)
            return ResolveAfterIndependentSearchFailure(
                CreateNoDoorBudgetFailure("low-structure-sparse-scale-seed"));

        // The cold path coarsely ranks the complete scale domain from this
        // sparse frame, then runs exact registration on at most three scales.
        // AlignStructureOnly reuses the prepared frame features and never
        // enters VPSG when this low-structure plan is active.
        var sparseSeedAttempt = _recognition.AlignFloorWithoutGates(
            frame,
            locked.Map.Id,
            floorKey,
            lowSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior: null,
            predictedViewportOrigin: null,
            liveIgnoreRegions: null,
            candidateHistory: null,
            isTracking: false,
            useProjectedBoundaryMask: false,
            scaleSearchPolicy: MapScaleSearchPolicy.Search,
            identityPriorConfidence,
            allowPrimaryFloor: true);
        StampCacheTrust(sparseSeedAttempt);
        var sparseScales = sparseSeedAttempt.StructureResult?.Candidates
            .Select(candidate => candidate.Scale)
            .Where(double.IsFinite)
            .DistinctBy(scale => Math.Round(scale, 6))
            .ToArray() ?? [];
        var operationKey = string.Join(
            "|",
            locked.Map.Id.ToString("D"),
            locked.Map.UpdatedAt.ToString("O"),
            floorKey,
            match.MatchId.ToString("D"),
            lowResolution.ClientWidth,
            lowResolution.ClientHeight,
            lowResolution.ViewportWidth,
            lowResolution.ViewportHeight,
            structureTuning.CacheFingerprint);
        _lowStructureRecoveryCursor.MarkSearched(operationKey, sparseScales);
        if (sparseSeedAttempt.Recognition is not null)
        {
            _lowStructureRecoveryCursor.Reset();
            return sparseSeedAttempt;
        }
        if (wallClock.ElapsedMilliseconds >= config.EndToEndBudgetMilliseconds)
            return ResolveAfterIndependentSearchFailure(sparseSeedAttempt);

        var preferredRecoveryScale = sparseSeedAttempt.StructureResult?.Candidates
            .OrderBy(candidate => candidate.CompositeCost)
            .Select(candidate => (double?)candidate.Scale)
            .FirstOrDefault() ?? lowSeed.ScaleX;

        var recoveryMinimumScale = Math.Min(
            config.MaximumScale,
            LowStructureAlignmentPlan.ResolveRecoveryMinimumScale(
                sparseSeedAttempt.StructureResult,
                config.MinimumScale));

        var recoveryGrid = MapStructureScaleEstimator.BuildCoarseGrid(
            recoveryMinimumScale,
            config.MaximumScale,
            config.ScaleHypothesisCount,
            config.MinimumUsableScale,
            preferredRecoveryScale);
        var batch = _lowStructureRecoveryCursor.TakeBatch(
            operationKey,
            recoveryGrid,
            config.MaximumScalesPerFrame);
        if (batch.Count == 0)
            return ResolveAfterIndependentSearchFailure(sparseSeedAttempt);

        var recoveryPlan = LowStructureAlignmentPlan.IncrementalRecovery(
            batch,
            batch: 1,
            config: config);
        var recovery = _recognition.AlignFloorWithoutGates(
            frame,
            locked.Map.Id,
            floorKey,
            lowSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior: null,
            predictedViewportOrigin: null,
            liveIgnoreRegions: null,
            candidateHistory: null,
            isTracking: false,
            useProjectedBoundaryMask: false,
            scaleSearchPolicy: MapScaleSearchPolicy.Search,
            identityPriorConfidence,
            allowPrimaryFloor: true,
            recoveryPlan);
        if (recovery.Recognition is not null)
            _lowStructureRecoveryCursor.Reset();
        return recovery.Recognition is not null
            ? StampCacheTrust(recovery)
            : ResolveAfterIndependentSearchFailure(StampCacheTrust(recovery));
    }

    /// <summary>
    /// cached-fixed-scale 单假设验证失败后的极小半径 Search 兜底。在缓存 scale
    /// 附近 ±3% 做诚实的结构搜索，救小漂移；成功/失败作为信任降级的验证证据。
    /// </summary>
    private MapRecognitionAttempt TryAlignWithCachedScaleRepairSearch(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        double cachedScale,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence)
    {
        if (!TryCreateNoDoorStageTuning(
                structureTuning,
                out var repairTuning,
                maximumStageMilliseconds:
                    MapOpenAlignmentRouteRules
                        .CachedScaleRepairSearchBudgetMilliseconds))
        {
            return CreateNoDoorBudgetFailure("cached-scale-repair-search");
        }
        MapOpenAlignmentRouteRules.ApplyCachedScaleRepairSearchPolicy(
            repairTuning);
        var repairSeed = MapFeatureCacheRules.CreateScaleSeed(
            locked.Map,
            floorKey,
            cachedScale);
        var repairSearch = _recognition.AlignFloorWithoutGates(
            frame,
            locked.Map.Id,
            floorKey,
            repairSeed,
            alignmentMode,
            tuning,
            repairTuning,
            candidateHistory: null,
            isTracking: false,
            scaleSearchPolicy: MapScaleSearchPolicy.Search,
            identityPriorConfidence: identityPriorConfidence);
        LogNoDoorStage(
            "cached-scale-repair-search",
            repairSearch.Recognition is not null,
            repairSearch,
            repairSearch.Diagnostics.TotalMilliseconds,
            new Dictionary<string, object?>
            {
                ["cachedScale"] = cachedScale,
                ["scaleRadius"] =
                    MapOpenAlignmentRouteRules.CachedScaleRepairSearchRadius,
                ["searchPolicy"] = nameof(MapScaleSearchPolicy.Search)
            });
        return repairSearch;
    }

    private async Task<(
        RuntimeMapRecognition? Recognition,
        string? FailureReason,
        MapScanDiagnostics? Diagnostics,
        MapFeatureCacheKey? RepairCacheKey,
        MapRecognitionAttempt? Attempt)> AlignQuickScanManualFloorAsync(
            CapturedGameFrame frame,
            RuntimeMapRecognition identityLock,
            CancellationToken cancellationToken)
    {
        if (_currentFloorKey is not { } manualFloorKey
            || string.Equals(
                manualFloorKey,
                identityLock.Result.Floor,
                StringComparison.Ordinal)
            || MapFloorRules.GetFloorProfile(
                identityLock.Map,
                manualFloorKey) is null)
        {
            return (identityLock, null, null, null, null);
        }

        var scaleSeed = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
            identityLock.Map,
            manualFloorKey);
        var recognitionTuning = CreateInitialAlignmentRecognitionTuning();
        var structureTuning = CreateStructureTuningForFloor(
            identityLock.Map,
            manualFloorKey,
            CreateInitialAlignmentStructureTuning());
        MapFeatureCacheKey? repairCacheKey = null;
        var floorDispatch = MapOperationTraceAmbient.StartChild(
            "floor_dispatch_wait",
            MapOperationWaitKind.Queue,
            mapId: identityLock.Map.Id.ToString("D"),
            floorKey: manualFloorKey);
        var attempt = await Task.Run(() =>
        {
            floorDispatch.Complete();
            using var floorWorker = MapOperationTraceAmbient.StartChild(
                "floor_worker_execution",
                MapOperationWaitKind.Compute,
                mapId: identityLock.Map.Id.ToString("D"),
                floorKey: manualFloorKey);
            return AlignExactManualFloor(
                frame,
                identityLock,
                manualFloorKey,
                scaleSeed,
                _settings!.OverlayAlignmentMode,
                recognitionTuning,
                structureTuning,
                0d,
                out repairCacheKey);
        }, cancellationToken);
        floorDispatch.Complete();
        if (attempt.Recognition is not null)
        {
            return (
                attempt.Recognition,
                null,
                attempt.Diagnostics,
                repairCacheKey,
                attempt);
        }

        var floorLabel = MapFloorRules.GetFloorDisplayName(
            identityLock.Map,
            manualFloorKey);
        return (
            null,
            $"地图已锁定，但按当前手动楼层 {floorLabel} 对齐失败："
                + attempt.FailureReason,
            attempt.Diagnostics,
            repairCacheKey,
            attempt);
    }

}
