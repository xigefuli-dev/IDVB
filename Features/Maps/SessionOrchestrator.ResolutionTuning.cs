using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Builds the per-run structure tuning from the persisted user baseline
    /// plus the active resolution preset. Keeping this as a copy avoids
    /// leaking one resolution's overrides into the next hot-swap.
    /// </summary>
    private MapStructureRegistrationTuning CreateEffectiveStructureTuning()
    {
        var tuning = _settings!.StructureRegistrationTuning.Clone();
        var structure = _config.Get<IDVBuff.Core.Models.StructureConfig>("structure");
        tuning.MaximumChamferPixels = structure.MaximumChamferPixels;
        tuning.RestrictedSearchMaximumChamferPixels =
            structure.RestrictedSearchMaximumChamferPixels;
        tuning.MinimumEdgeCoverage = structure.MinimumEdgeCoverage;
        tuning.MinimumOccupancyCoverage = structure.MinimumOccupancyCoverage;
        tuning.MinimumCandidateMargin = structure.MinimumCandidateMargin;
        tuning.EdgeDistanceTolerancePixels = structure.EdgeDistanceTolerancePixels;
        tuning.DistanceClipPixels = structure.DistanceClipPixels;

        var composite = _config.Get<CompositeCostConfig>("composite_cost");
        tuning.ChamferWeight = composite.ChamferWeight;
        tuning.EdgeCoverageWeight = composite.EdgeCoverageWeight;
        tuning.OccupancyCoverageWeight = composite.OccupancyWeight;
        tuning.ReferenceCoverageWeight = composite.EdgeCoverageWeight;
        tuning.PartitionPenaltyWeight = composite.PartitionWeight;
        tuning.PriorDisagreementWeight = composite.PriorDisagreementWeight;
        tuning.BoundsPenalty = composite.BoundsPenalty;

        var partitions = _config.Get<PartitionsConfig>("partitions");
        tuning.MinimumEdgesPerPartition = partitions.MinEdgesPerPartition;
        tuning.MinimumPartitionCoverage = partitions.MinCoverage;

        var scale = _config.Get<IDVBuff.Core.Models.ScaleConfig>("scale");
        tuning.ScaleSearchRadius = scale.SearchRadius;
        tuning.ScaleSearchStep = scale.SearchStep;
        tuning.TrackingScaleSearchRadius = scale.TrackingScaleSearchRadius;

        var coarse = _config.Get<IDVBuff.Core.Models.CoarseConfig>("coarse");
        tuning.EnableFastAlignment = coarse.EnableFastAlignment;
        tuning.FastFallbackToLegacy = coarse.FastFallbackToLegacy;
        tuning.FastCoarseMaxDimension = coarse.FastCoarseMaxDimension;
        tuning.FastCoarseDownsampleFactor = coarse.FastCoarseDownsampleFactor;
        tuning.FastCoarseTopK = coarse.FastCoarseTopK;
        tuning.FastCoarseNmsRadius = coarse.FastCoarseNmsRadius;

        var ecc = _config.Get<IDVBuff.Core.Models.EccConfig>("ecc");
        tuning.EnableEccRefinement = ecc.EnableEccRefinement;
        tuning.SkipEccScoreThreshold = ecc.SkipEccScoreThreshold;

        var feature = _config.Get<FeatureVotingConfig>("feature_voting");
        tuning.EnableFeatureVoting = feature.Enable;
        tuning.FeatureRatioThreshold = feature.RatioThreshold;
        tuning.FeatureInlierTolerancePixels = feature.InlierTolerancePixels;

        var early = _config.Get<EarlyTerminationConfig>("early_termination");
        tuning.EarlyTerminationScoreThreshold = early.ScoreThreshold;

        var visible = _config.Get<VisibleAwareConfig>("visible_aware");
        tuning.EnableVisibleMask = visible.EnableMask;
        tuning.EnableVisibleAwareShadow = visible.EnableShadow;
        tuning.EnableVisibleAwareInjection = visible.EnableInjection;
        tuning.EnableVisibleAwareEarlyExit = visible.EnableEarlyExit;
        tuning.VisibleAwareSearchBudgetMilliseconds = visible.SearchBudgetMs;
        tuning.VisibleAwareCoarseDownsample = visible.CoarseDownsample;
        tuning.VisibleAwareTopK = visible.TopK;
        tuning.VisibleAwareMinimumVisibleFraction = visible.MinVisibleFraction;
        tuning.VisibleAwareMinimumVisibleStructurePixels =
            visible.MinVisibleStructurePixels;
        tuning.SafeVisibleMaskErodePixels = visible.SafeErodePixels;
        tuning.VisibleVMin = visible.VMin;
        tuning.VisibleSMin = visible.SMin;
        tuning.VisibleHighlightVMin = visible.HighlightVMin;
        tuning.VisibleAwareEarlyTerminationMaxCompositeCost =
            visible.EarlyTerminationMaxCompositeCost;

        tuning.Normalize();
        return tuning;
    }

    private MapStructureRegistrationTuning CreateStructureTuningForFloor(
        MapRecord map,
        string floorKey,
        MapStructureRegistrationTuning tuning)
    {
        var channel = MapAlignmentChannelRegistry.Resolve(map, floorKey);
        if (channel.Channel != MapAlignmentChannel.LowStructure)
            return tuning;

        return MapAlignmentChannelRegistry.CreateLowStructure(
            _config.Get<LowStructureConfig>("low_structure"));
    }

    private MapStructureRegistrationTuning CreateInitialAlignmentStructureTuning()
    {
        var tuning = CreateEffectiveStructureTuning();
        // Initial alignment participates in the same end-to-end budget as
        // tracking and recovery. Do not silently expand the persisted 1.5s
        // budget to the former 3s legacy fallback.
        tuning.Normalize();
        return tuning;
    }

    private static MapStructureRegistrationTuning CreateScanVerificationTuning(
        MapStructureRegistrationTuning source)
    {
        var tuning = source.Clone();
        tuning.Mode = MapStructureRegistrationMode.ScanVerification;
        tuning.EnableScanCheapRejectShadowCollection = true;
        tuning.StructureFallbackBudgetMilliseconds = 100;
        tuning.EnableFeatureVoting = false;
        tuning.EnableEccRefinement = false;
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = false;
        tuning.FastAlignmentShadowMode = false;
        tuning.FastCoarseTopK = 2;
        tuning.MaximumTranslationCandidates = 2;
        tuning.TopCandidateCount = 2;
        tuning.PreviousAlignmentSearchRadiusPixels = 48;
        tuning.DisableScaleEarlyTermination = false;
        tuning.EnableVisibleMask = false;
        tuning.EnableVisibleAwareShadow = false;
        tuning.EnableVisibleAwareInjection = false;
        tuning.EnableVisibleAwareEarlyExit = false;
        tuning.Normalize();
        return tuning;
    }

    private MapRecognitionTuning CreateInitialAlignmentRecognitionTuning()
    {
        var tuning = _settings!.RecognitionTuning.Clone();
        tuning.WarmGateSearchBudgetMs = Math.Max(
            500,
            tuning.WarmGateSearchBudgetMs);
        tuning.Normalize();
        return tuning;
    }
}
/*
 * 文件职责：SessionOrchestrator.ResolutionTuning。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
