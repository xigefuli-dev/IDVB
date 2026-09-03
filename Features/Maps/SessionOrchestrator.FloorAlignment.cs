using IDVBuff.Pipeline;
using System.Diagnostics;

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
        structureTuning = CreateStructureTuningForFloor(
            locked.Map,
            floorKey,
            structureTuning);
        var alignmentChannel = MapAlignmentChannelRegistry.Resolve(
            locked.Map,
            floorKey);
        if (alignmentChannel.Channel == MapAlignmentChannel.LowStructure)
            return AlignLowStructureFloor(
                frame,
                locked,
                floorKey,
                alignmentMode,
                tuning,
                structureTuning,
                identityPriorConfidence,
                out repairCacheKey);
        // 缓存信任门控：fixed/兜底连续失败达阈值后，本轮已无可用缓存证据，
        // 跳过 VPSG 把预算直接给宽半径全局恢复。
        var skipVpsgForDistrustedCache = false;
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        var hasFloorCalibration = _settings!.FloorScaleCalibrations
            .Any(calibration => calibration.Matches(
                locked.Map.Id,
                locked.Map.UpdatedAt,
                primaryFloorKey,
                floorKey));

        var operationMatch = _matchSession.Snapshot;
        var hasAdaptiveSeed = TryAlignWithAdaptiveCalibrationSeed(
            frame,
            locked.Map,
            floorKey,
            alignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence,
            out var adaptiveSeed,
            out var adaptiveAttempt);
        if (hasAdaptiveSeed && adaptiveSeed is not null)
            scaleSeed = MapFeatureCacheRules.CreateScaleSeed(
                locked.Map,
                floorKey,
                adaptiveSeed.Scale);
        if (adaptiveAttempt is not null
            && IsAdaptiveInitialScaleQualified(adaptiveAttempt, structureTuning))
            return adaptiveAttempt;
        var reliable = hasAdaptiveSeed
            ? null
            : TryGetReliableFloorAlignment(
                operationMatch,
                frame,
                locked.Map,
                floorKey,
                out _);
        if (reliable is not null
            && TryGetPendingMapCacheRepairKey(
                frame,
                locked.Map,
                floorKey,
                out var pendingRepairKey))
        {
            repairCacheKey = pendingRepairKey;
        }
        MapRecognitionAttempt? firstAttempt = adaptiveAttempt;
        double? vpsgScale = null;
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
        // 自适应种子存在但没过高置信资格门时，仍应尝试可信缩放缓存（其 Usable
        // 门槛更宽松，是已验证证据）。此前这里带 `!hasAdaptiveSeed`，会在自适应
        // 路径未过资格门时把这条快路径整个跳过，使无门楼层直接落到 VPSG + 全局
        // 恢复的慢路径（实测 2F ~340ms → ~1000ms）。自适应路径只有资格门通过才
        // 提前返回，走到这里即未通过，此时回落可信缓存是正确的快速兜底。
        else if (TryGetNoDoorScaleCache(
                     frame,
                     locked.Map,
                     floorKey,
                     out var cacheKey,
                     out var cacheEntry)
                 && cacheKey is not null
                 && cacheEntry is not null)
        {
            // 信任门槛：连续验证失败达到阈值的条目跳过 fixed 验证。
            // repairCacheKey 保留，使修复样本继续积累，最终由 Recovery 替换毒缓存。
            if (!MapFeatureCacheRules.IsCacheEntryTrusted(cacheEntry))
            {
                repairCacheKey = cacheKey;
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Warning,
                    $"缩放缓存已降级跳过 · floor={floorKey} · "
                    + $"scale={cacheEntry.Scale.UniformScale:F6}",
                    details: new()
                    {
                        ["mapId"] = locked.Map.Id,
                        ["floor"] = floorKey,
                        ["scale"] = cacheEntry.Scale.UniformScale,
                        ["source"] = cacheEntry.Scale.Source.ToString(),
                        ["failedValidationCount"] =
                            cacheEntry.Scale.Validation?.FailedValidationCount,
                        ["cacheDecision"] = "distrusted-skipped"
                    });
            }
            else
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
                // 缓存命中验证用宽松门槛（RecoveryConfidence 0.65），与主楼层
                // AlignUsingScaleCache 保持一致。缓存 scale 本身就是高置信证据，
                // 固定 scale 结构验证到 0.77~0.81 即可采纳。此前这里用 Qualified
                // (0.82) 会把 0.777 的高质量结果拒掉，随后串行白跑 repair-search
                // → VPSG → global-recovery 三连（实测 92ms 该完成，最终 820ms），
                // 而且最后无门槛接受的 global-recovery 只有 0.745，比被拒的更差。
                if (IsAdaptiveInitialScaleUsable(firstAttempt, cachedTuning)
                    && firstAttempt.Recognition is { } cachedRecognition)
                {
                    return CopyAttempt(
                        firstAttempt,
                        MarkUsedCachedScale(cachedRecognition));
                }

                // fixed 单假设验证失败 → 缓存 scale 附近极小半径 Search 兜底，
                // 救小漂移；同时为信任降级提供验证证据（成功重置 / 失败 +1）。
                var repairSearch = TryAlignWithCachedScaleRepairSearch(
                    frame,
                    locked,
                    floorKey,
                    cacheEntry.Scale.UniformScale,
                    alignmentMode,
                    tuning,
                    structureTuning,
                    identityPriorConfidence);
                // repair search 与 cached-fixed-scale 同为「缓存 scale 验证」，
                // 用同一宽松门槛（0.65）：它救的正是缓存 scale 的小漂移，结果
                // 0.72 这类边缘置信命中应直接采纳，而不是被 0.82 拒后继续 VPSG。
                if (IsAdaptiveInitialScaleUsable(repairSearch, structureTuning)
                    && repairSearch.Recognition is { } repairRecognition)
                {
                    NoteCacheValidationOutcome(cacheKey, succeeded: true);
                    return CopyAttempt(
                        repairSearch,
                        MarkUsedCachedScale(repairRecognition));
                }

                NoteCacheValidationOutcome(cacheKey, succeeded: false);
                // The entry stays active and trusted. A later successful global
                // recovery is merely a repair candidate and cannot replace a
                // manual entry until three consistent samples have accumulated.
                repairCacheKey = cacheKey;
                MarkMapCacheForRepair(cacheKey);
                // 本轮失败后计数将达阈值 → 缓存已确认不可靠，跳过 VPSG 直达
                // 宽半径全局恢复（止血，避免再消耗 VPSG 预算）。
                skipVpsgForDistrustedCache =
                    (cacheEntry.Scale.Validation?.FailedValidationCount ?? 0) + 1
                        >= MapFeatureCacheRules
                            .MaximumFailedValidationCountBeforeDistrust;
            }
        }

        // VPSG 缩放引导：不信任跨楼层 scale seed，用本楼层边缘结构直接
        // 估计 scale 并做固定 scale 结构验证。成功则短路返回。
        if (!skipVpsgForDistrustedCache
            && TryAlignFloorWithVpsg(
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
            if (vpsgAttempt.Recognition is not null
                && IsAdaptiveInitialScaleQualified(vpsgAttempt, structureTuning))
                return vpsgAttempt;
            // VPSG 估计出本楼层 scale 但固定 scale 结构验证失败：保留估计值，
            // 让全局恢复以它（而非中性 KEEP-1.0 种子）为搜索锚点，避免正确
            // scale 落在 [0.40,1.60] 搜索范围之外。
            if (double.IsFinite(vpsgAttempt.Diagnostics.ScaleBootstrapScale)
                && vpsgAttempt.Diagnostics.ScaleBootstrapScale > 0d)
            {
                vpsgScale = vpsgAttempt.Diagnostics.ScaleBootstrapScale;
            }
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
        var recoverySeed = vpsgScale is { } vpsgEstimate
            ? MapFeatureCacheRules.CreateScaleSeed(locked.Map, floorKey, vpsgEstimate)
            : scaleSeed;
        var recovery = _recognition.AlignFloorWithoutGates(
            frame,
            locked.Map.Id,
            floorKey,
            recoverySeed,
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

}
/*
 * 文件职责：SessionOrchestrator.FloorAlignment。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
