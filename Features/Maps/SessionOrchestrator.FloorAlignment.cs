namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static void AccumulateNoDoorStageTimings(
        MapScanDiagnostics source,
        MapScanDiagnostics destination)
    {
        destination.ReferenceImageLoadMilliseconds +=
            source.ReferenceImageLoadMilliseconds;
        destination.ReferenceCacheMilliseconds +=
            source.ReferenceCacheMilliseconds;
        destination.CacheMilliseconds += source.CacheMilliseconds;
        destination.StructurePreprocessMilliseconds +=
            source.StructurePreprocessMilliseconds;
        destination.LiveStructurePreprocessMilliseconds +=
            source.LiveStructurePreprocessMilliseconds;
        destination.StructureSearchMilliseconds +=
            source.StructureSearchMilliseconds;
        destination.StructureRefineMilliseconds +=
            source.StructureRefineMilliseconds;
        destination.AuxiliaryAnchorMilliseconds +=
            source.AuxiliaryAnchorMilliseconds;
        destination.TotalMilliseconds += source.TotalMilliseconds;
    }

    private MapRecognitionAttempt AlignExactManualFloor(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        out MapFeatureCacheKey? repairCacheKey)
    {
        repairCacheKey = null;
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        var hasFloorCalibration = _settings!.FloorScaleCalibrations
            .Any(calibration => calibration.Matches(
                locked.Map.Id,
                locked.Map.UpdatedAt,
                primaryFloorKey,
                floorKey));

        var operationMatch = _matchSession.Snapshot;
        var reliable = TryGetReliableFloorAlignment(
            operationMatch,
            locked.Map,
            floorKey);
        if (reliable is not null
            && TryGetPendingMapCacheRepairKey(
                frame,
                locked.Map,
                floorKey,
                out var pendingRepairKey))
        {
            repairCacheKey = pendingRepairKey;
        }
        MapRecognitionAttempt? firstAttempt = null;
        if (reliable is not null)
        {
            firstAttempt = AlignNoDoorLocalStructure(
                frame,
                locked,
                floorKey,
                reliable.Session,
                alignmentMode,
                tuning,
                structureTuning,
                reliable.CandidateHistory,
                identityPriorConfidence);
            LogNoDoorStage(
                "same-floor-local",
                firstAttempt.Recognition is not null,
                firstAttempt,
                firstAttempt.Diagnostics.TotalMilliseconds,
                new Dictionary<string, object?>
                {
                    ["historyCount"] = reliable.CandidateHistory.Count,
                    ["scale"] = reliable.Session.LockedTransform.ScaleX
                });
            if (firstAttempt.Recognition is not null)
                return firstAttempt;
            scaleSeed = reliable.Session.LockedTransform;
        }
        else if (TryGetNoDoorScaleCache(
                     frame,
                     locked.Map,
                     floorKey,
                     out var cacheKey,
                     out var cacheEntry)
                 && cacheKey is not null
                 && cacheEntry is not null)
        {
            if (!TryCreateNoDoorStageTuning(
                    structureTuning,
                    out var cachedTuning,
                    maximumStageMilliseconds: 650))
            {
                return CreateNoDoorBudgetFailure("cached-fixed-scale");
            }

            scaleSeed = MapFeatureCacheRules.CreateScaleSeed(
                locked.Map,
                floorKey,
                cacheEntry.Scale.UniformScale);
            firstAttempt = _recognition.AlignWithCachedScale(
                frame,
                locked.Map.Id,
                floorKey,
                scaleSeed,
                alignmentMode,
                tuning,
                cachedTuning,
                identityPriorConfidence);
            LogNoDoorStage(
                "cached-fixed-scale",
                firstAttempt.Recognition is not null,
                firstAttempt,
                firstAttempt.Diagnostics.TotalMilliseconds,
                new Dictionary<string, object?>
                {
                    ["scale"] = cacheEntry.Scale.UniformScale,
                    ["cacheSource"] = cacheEntry.Scale.Source.ToString()
                });
            if (firstAttempt.Recognition is { } cachedRecognition)
            {
                return CopyAttempt(
                    firstAttempt,
                    MarkUsedCachedScale(cachedRecognition));
            }

            // The entry stays active and trusted. A later successful global
            // recovery is merely a repair candidate and cannot replace a
            // manual entry until three consistent samples have accumulated.
            repairCacheKey = cacheKey;
            MarkMapCacheForRepair(cacheKey);
        }

        // VPSG 缩放引导：不信任跨楼层 scale seed，用 AKAZE 描述符几何直接
        // 估计本楼层 scale 并做固定 scale 结构验证。成功则短路返回。
        if (TryAlignFloorWithVpsg(
                frame,
                locked,
                floorKey,
                scaleSeed,
                alignmentMode,
                tuning,
                structureTuning,
                identityPriorConfidence) is { } vpsgAttempt)
        {
            firstAttempt ??= vpsgAttempt;
            if (vpsgAttempt.Recognition is not null)
                return vpsgAttempt;
        }

        // 辅助锚点已停用：跳过 auxiliary-disambiguation 阶段，
        // 直接使用结构搜索种子进行全局恢复。

        if (!TryCreateNoDoorStageTuning(
                structureTuning,
                out var recoveryTuning))
        {
            return CreateNoDoorBudgetFailure(
                "single-global-recovery",
                firstAttempt?.Diagnostics);
        }

        var recoveryRadius =
            MapOpenAlignmentRouteRules.ResolveSingleGlobalRecoveryRadius(
                hasFloorCalibration);
        recoveryTuning.ScaleSearchRadius = recoveryRadius;
        recoveryTuning.TrackingScaleSearchRadius = 0d;
        // VPSG 失败保护：seed scale 可能错误（跨楼层 KEEP-1.0）。全局恢复必须做
        // 真正的 scale 搜索，禁止固定 scale 快速粗搜索与单假设早停。
        recoveryTuning.EnableFastAlignment = false;
        recoveryTuning.DisableScaleEarlyTermination = true;
        if (NoDoorAlignmentDeadline.Current is { } recoveryDeadline
            && recoveryDeadline.RemainingMilliseconds
                < MapOpenAlignmentRouteRules
                    .MinimumFeatureRecoveryBudgetMilliseconds)
        {
            // Preserve the actual global search when an earlier fixed/local
            // attempt consumed most of the budget. Reusing edge features is
            // more useful here than spending the remainder on AKAZE alone.
            recoveryTuning.EnableFeatureVoting = false;
        }
        recoveryTuning.Normalize();
        var recovery = _recognition.AlignFloorWithoutGates(
            frame,
            locked.Map.Id,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            recoveryTuning,
            candidateHistory: reliable?.CandidateHistory,
            isTracking: false,
            scaleSearchPolicy: MapScaleSearchPolicy.Search,
            identityPriorConfidence: identityPriorConfidence);
        if (firstAttempt is not null)
        {
            AccumulateNoDoorStageTimings(
                firstAttempt.Diagnostics,
                recovery.Diagnostics);
        }
        if (NoDoorAlignmentDeadline.Current?.IsExpired == true
            && recovery.Recognition is null)
        {
            return CreateNoDoorBudgetFailure(
                "single-global-recovery",
                recovery.Diagnostics);
        }
        LogNoDoorStage(
            "single-global-recovery",
            recovery.Recognition is not null,
            recovery,
            recovery.Diagnostics.TotalMilliseconds,
            new Dictionary<string, object?>
            {
                ["scaleRadius"] = recoveryRadius,
                ["historyCount"] = reliable?.CandidateHistory.Count ?? 0,
                ["featureBootstrapAttempted"] = false,
                ["featureVotingEnabled"] = recoveryTuning.EnableFeatureVoting
            });
        return recovery;
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

        if (identityLock.Result.OverlayTransform is not { } identityTransform)
        {
            return (
                null,
                $"地图已锁定，但当前手动楼层 {manualFloorKey.ToUpperInvariant()} 缺少可用缩放种子。",
                null,
                null,
                new MapRecognitionAttempt
                {
                    FailureReason = $"Manual floor {manualFloorKey} has no usable scale seed."
                });
        }

        var scaleSeed = CreateCrossFloorScaleSeed(
            identityLock.Map,
            identityLock.Result.Floor,
            manualFloorKey,
            identityTransform);
        var recognitionTuning = CreateInitialAlignmentRecognitionTuning();
        var structureTuning = CreateInitialAlignmentStructureTuning();
        using var deadline = new NoDoorAlignmentDeadline(
            cancellationToken,
            structureTuning.StructureFallbackBudgetMilliseconds);
        MapFeatureCacheKey? repairCacheKey = null;
        var attempt = await Task.Run(() =>
        {
            using var ambientDeadline = deadline.EnterAmbient();
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

    private static MapOverlayTransform CreateCrossFloorScaleSeed(
        MapRecord map,
        string sourceFloorKey,
        string targetFloorKey,
        MapOverlayTransform sourceTransform)
    {
        var sourceFloor = MapFloorRules.GetFloorProfile(map, sourceFloorKey);
        var targetFloor = MapFloorRules.GetFloorProfile(map, targetFloorKey);
        return sourceFloor is not null && targetFloor is not null
            ? MapFloorScaleSeedRules.RenormalizeTransformToFloor(
                sourceTransform,
                sourceFloor,
                targetFloor)
            : sourceTransform;
    }
}
