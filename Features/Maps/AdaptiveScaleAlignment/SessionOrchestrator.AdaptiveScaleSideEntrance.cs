namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignSideEntranceWithScaleFallback(
        CapturedGameFrame frame,
        SideEntranceScanCandidate candidate,
        MapAlignmentSession templateSeed,
        MapRecognitionTuning alignmentTuning,
        MapStructureRegistrationTuning structureTuning,
        out MapAlignmentSession usedSeed)
    {
        usedSeed = templateSeed;
        var targetResolution = GetResolution(frame);
        var rejectionChain = new List<string>();
        var hasAdaptiveSeed = TryAlignWithAdaptiveCalibrationSeed(
            frame,
            candidate.Map,
            candidate.FloorKey,
            _settings!.OverlayAlignmentMode,
            alignmentTuning,
            structureTuning,
            candidate.MatchScore,
            out var adaptiveSeed,
            out var adaptiveAttempt);
        if (hasAdaptiveSeed && adaptiveSeed is not null)
        {
            var adaptiveSession = templateSeed.WithUniformScale(adaptiveSeed.Scale);
            LogScaleSeedDecision(
                candidate,
                adaptiveSeed.Source == AdaptiveScaleAlignment.AdaptiveScaleSeedSource.Runtime
                    ? "adaptive-runtime"
                    : "adaptive-calibration",
                adaptiveSeed.Scale,
                null,
                targetResolution,
                adaptiveAttempt,
                string.Empty);
            if (adaptiveAttempt?.StructureAccepted == true
                && adaptiveAttempt.Recognition is not null
                && IsAdaptiveInitialScaleQualified(adaptiveAttempt, structureTuning))
            {
                usedSeed = adaptiveSession;
                return adaptiveAttempt;
            }
            rejectionChain.Add(
                adaptiveAttempt is null
                    ? "adaptive:unavailable"
                    : $"adaptive:{DescribeAttemptFailure(adaptiveAttempt)}");
        }

        ResolvedMapScaleSeed? cacheSeed = null;
        var cacheRejection = targetResolution.IsSupported
            ? string.Empty
            : "unsupported-target-resolution";
        if (!hasAdaptiveSeed && targetResolution.IsSupported)
        {
            var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(candidate.Map);
            var entries = _mapFeatureCacheRepository.GetSnapshot(
                candidate.Map.Id,
                fingerprint,
                candidate.FloorKey);
            MapScaleSeedResolver.TryResolve(
                entries,
                candidate.Map.Id,
                fingerprint,
                candidate.FloorKey,
                targetResolution,
                _settings.SessionTuning.HighConfidence,
                structureTuning.MinimumCandidateMargin,
                out cacheSeed,
                out cacheRejection);
        }

        if (cacheSeed is not null)
        {
            var projectedSeed = templateSeed.WithUniformScale(cacheSeed.Scale);
            var cacheAttempt = AlignSideEntranceFromSeed(
                frame,
                candidate,
                projectedSeed,
                alignmentTuning,
                structureTuning);
            SetScaleSeedDiagnostics(cacheAttempt, cacheSeed, cacheRejection);
            LogScaleSeedDecision(
                candidate,
                cacheSeed.Source == MapScaleSeedSource.ExactCache
                    ? "exact-cache"
                    : "cross-resolution",
                cacheSeed.Scale,
                cacheSeed.SourceResolution,
                targetResolution,
                cacheAttempt,
                cacheRejection);
            if (cacheAttempt.StructureAccepted
                && cacheAttempt.Recognition is { } cacheRecognition
                && IsAdaptiveInitialScaleQualified(cacheAttempt, structureTuning))
            {
                usedSeed = projectedSeed;
                cacheAttempt = CopyAttempt(
                    cacheAttempt,
                    MarkUsedCachedScale(cacheRecognition));
                if (cacheSeed.IsProjected)
                    StageCrossResolutionValidatedScale(
                        frame,
                        candidate,
                        targetResolution,
                        cacheAttempt);
                return cacheAttempt;
            }
            rejectionChain.Add(
                $"{ScaleSeedSourceName(cacheSeed.Source)}:{DescribeAttemptFailure(cacheAttempt)}");
        }
        else if (!hasAdaptiveSeed)
        {
            rejectionChain.Add($"cache:{cacheRejection}");
            LogScaleSeedDecision(
                candidate,
                "cache-rejected",
                double.NaN,
                null,
                targetResolution,
                null,
                cacheRejection);
        }

        var strictVpsgTuning = MapScaleSeedResolver
            .CreateStrictVpsgValidationTuning(structureTuning);
        var vpsgAttempt = _recognition.AlignLockedFloorFeature(
            frame,
            candidate.Map.Id,
            candidate.FloorKey,
            templateSeed.LockedTransform,
            _settings.OverlayAlignmentMode,
            alignmentTuning,
            strictVpsgTuning,
            candidate.MatchScore);
        SetScaleSeedDiagnostics(
            vpsgAttempt,
            MapScaleSeedSource.Vpsg,
            vpsgAttempt.Diagnostics.ScaleBootstrapScale,
            cacheSeed?.SourceResolution,
            targetResolution,
            string.Join(";", rejectionChain),
            cacheSeed?.CacheSource.ToString() ?? string.Empty,
            cacheSeed?.IsProjected ?? false,
            cacheSeed is { IsProjected: true } ? cacheSeed.Scale : 0d);
        LogScaleSeedDecision(
            candidate,
            "vpsg",
            vpsgAttempt.Diagnostics.ScaleBootstrapScale,
            null,
            targetResolution,
            vpsgAttempt,
            string.Join(";", rejectionChain));
        if (vpsgAttempt.StructureAccepted
            && vpsgAttempt.Recognition is not null
            && IsAdaptiveInitialScaleQualified(vpsgAttempt, structureTuning))
            return vpsgAttempt;
        rejectionChain.Add($"vpsg:{DescribeAttemptFailure(vpsgAttempt)}");

        var templateAttempt = AlignSideEntranceFromSeed(
            frame,
            candidate,
            templateSeed,
            alignmentTuning,
            structureTuning);
        SetScaleSeedDiagnostics(
            templateAttempt,
            MapScaleSeedSource.SideTemplate,
            templateSeed.LockedTransform.ScaleX,
            cacheSeed?.SourceResolution,
            targetResolution,
            string.Join(";", rejectionChain),
            cacheSeed?.CacheSource.ToString() ?? string.Empty,
            cacheSeed?.IsProjected ?? false,
            cacheSeed is { IsProjected: true } ? cacheSeed.Scale : 0d);
        LogScaleSeedDecision(
            candidate,
            "side-template",
            templateSeed.LockedTransform.ScaleX,
            null,
            targetResolution,
            templateAttempt,
            string.Join(";", rejectionChain));
        return templateAttempt;
    }
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScaleSideEntrance。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
