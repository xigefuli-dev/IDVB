using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignSideEntranceWithScaleFallback(
        CapturedGameFrame frame,
        SideEntranceScanCandidate candidate,
        MapAlignmentSession templateSeed,
        MapRecognitionTuning alignmentTuning,
        MapStructureRegistrationTuning structureTuning,
        bool allowVpsgRescue,
        out MapAlignmentSession usedSeed)
    {
        usedSeed = templateSeed;
        if (MapAlignmentChannelRegistry.Resolve(
                candidate.Map,
                candidate.FloorKey).Channel == MapAlignmentChannel.LowStructure)
        {
            // Low-structure floors have no door evidence. Keep this defensive
            // boundary before adaptive calibration, cache, or VPSG can run.
            var lowStructureTuning = structureTuning.Clone();
            lowStructureTuning.Channel = MapAlignmentChannel.LowStructure;
            lowStructureTuning.EnableFeatureVoting = false;
            lowStructureTuning.LowStructureEnableFeatureScaleEstimate = false;
            lowStructureTuning.Normalize();
            usedSeed = templateSeed.WithUniformScale(
                MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                    candidate.Map,
                    candidate.FloorKey).ScaleX);
            return _recognition.AlignFloorWithoutGates(
                frame,
                candidate.Map.Id,
                candidate.FloorKey,
                usedSeed.LockedTransform,
                _settings!.OverlayAlignmentMode,
                alignmentTuning,
                lowStructureTuning,
                identityPriorConfidence: candidate.MatchScore,
                allowPrimaryFloor: true);
        }
        var targetResolution = GetResolution(frame);
        var rejectionChain = new List<string>();
        var isScanVerification = structureTuning.Mode ==
            MapStructureRegistrationMode.ScanVerification;
        MapRecognitionAttempt? templateAttempt = null;
        MapRecognitionAttempt? lastScanAttempt = null;
        if (isScanVerification)
        {
            var templateTimer = Stopwatch.StartNew();
            templateAttempt = AlignSideEntranceFromSeed(
                frame,
                candidate,
                templateSeed,
                alignmentTuning,
                structureTuning);
            templateTimer.Stop();
            lastScanAttempt = templateAttempt;
            PopulateScanAttemptTiming(
                templateAttempt,
                templateTimer.Elapsed.TotalMilliseconds,
                vpsgMilliseconds: 0d,
                vpsgAttempted: false);
            LogScaleSeedDecision(
                candidate,
                "side-template",
                templateSeed.LockedTransform.ScaleX,
                null,
                targetResolution,
                templateAttempt,
                string.Empty);
            var templateAdaptiveQualified = IsAdaptiveInitialScaleQualified(
                templateAttempt,
                structureTuning);
            var templateFormalAccepted = MapOpenAlignmentRouteRules
                .ShouldShortCircuitScanVerification(templateAttempt);
            LogScanVerificationStage(
                "scan_template_formal",
                candidate,
                templateAttempt,
                templateAdaptiveQualified,
                shortCircuited: false);
            LogScanVerificationStage(
                "scan_template_acceptance",
                candidate,
                templateAttempt,
                templateAdaptiveQualified,
                shortCircuited: templateFormalAccepted);
            if (templateFormalAccepted)
            {
                return templateAttempt;
            }
            rejectionChain.Add(
                $"side-template:{DescribeAttemptFailure(templateAttempt)}");
            if (templateAttempt.Diagnostics.ScanCheapRejected)
                return templateAttempt;
            if (!allowVpsgRescue)
                return templateAttempt;
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is { } templateRemaining
                && templateRemaining < MapOpenAlignmentRouteRules
                    .ScanVerificationMinimumVpsgBudgetMilliseconds)
            {
                return templateAttempt;
            }
        }

        AdaptiveScaleSeedDecision? adaptiveSeed = null;
        MapRecognitionAttempt? adaptiveAttempt = null;
        var hasAdaptiveSeed = false;
        if (!isScanVerification)
        {
            hasAdaptiveSeed = TryAlignWithAdaptiveCalibrationSeed(
                frame,
                candidate.Map,
                candidate.FloorKey,
                _settings!.OverlayAlignmentMode,
                alignmentTuning,
                structureTuning,
                candidate.MatchScore,
                out adaptiveSeed,
                out adaptiveAttempt);
        }
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
                _settings!.SessionTuning.HighConfidence,
                structureTuning.MinimumCandidateMargin,
                out cacheSeed,
                out cacheRejection);
        }

        if (cacheSeed is not null)
        {
            var exactSeed = templateSeed.WithUniformScale(cacheSeed.Scale);
            var cacheAttempt = AlignSideEntranceFromSeed(
                frame,
                candidate,
                exactSeed,
                alignmentTuning,
                structureTuning);
            lastScanAttempt = cacheAttempt;
            if (isScanVerification && templateAttempt is not null)
                MergeScanVerificationCounters(
                    cacheAttempt.Diagnostics,
                    templateAttempt.Diagnostics);
            PopulateScanAttemptTiming(
                cacheAttempt,
                templateAttempt is null
                    ? 0d
                    : templateAttempt.Diagnostics.ScanTemplateValidationMilliseconds,
                vpsgMilliseconds: 0d,
                vpsgAttempted: false);
            SetScaleSeedDiagnostics(cacheAttempt, cacheSeed, cacheRejection);
            LogScaleSeedDecision(
                candidate,
                "exact-cache",
                cacheSeed.Scale,
                cacheSeed.SourceResolution,
                targetResolution,
                cacheAttempt,
                cacheRejection);
            var cacheAdaptiveQualified = IsAdaptiveInitialScaleQualified(
                cacheAttempt,
                structureTuning);
            var cacheFormalAccepted = MapOpenAlignmentRouteRules
                .ShouldShortCircuitScanVerification(cacheAttempt);
            LogScanVerificationStage(
                "scan_cache_formal",
                candidate,
                cacheAttempt,
                cacheAdaptiveQualified,
                shortCircuited: false);
            LogScanVerificationStage(
                "scan_cache_acceptance",
                candidate,
                cacheAttempt,
                cacheAdaptiveQualified,
                shortCircuited: isScanVerification && cacheFormalAccepted);
            if (isScanVerification && cacheFormalAccepted)
            {
                usedSeed = exactSeed;
                cacheAttempt = CopyAttempt(
                    cacheAttempt,
                    MarkUsedCachedScale(cacheAttempt.Recognition!));
                return cacheAttempt;
            }
            if (cacheAdaptiveQualified
                && cacheAttempt.Recognition is { } cacheRecognition)
            {
                usedSeed = exactSeed;
                cacheAttempt = CopyAttempt(
                    cacheAttempt,
                    MarkUsedCachedScale(cacheRecognition));
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

        if (isScanVerification
            && MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is { } remaining
            && remaining < MapOpenAlignmentRouteRules
                .ScanVerificationMinimumVpsgBudgetMilliseconds)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "扫描 VPSG rescue 因剩余预算不足而跳过",
                details: new()
                {
                    ["remainingMs"] = remaining,
                    ["minimumVpsgMs"] = MapOpenAlignmentRouteRules
                        .ScanVerificationMinimumVpsgBudgetMilliseconds
                });
            return lastScanAttempt ?? templateAttempt!;
        }

        var strictVpsgTuning = MapScaleSeedResolver
            .CreateStrictVpsgValidationTuning(structureTuning);
        if (isScanVerification)
        {
            strictVpsgTuning.StructureFallbackBudgetMilliseconds = Math.Min(
                MapOpenAlignmentRouteRules.ScanVerificationVpsgBudgetMilliseconds,
                MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
                    ?? MapOpenAlignmentRouteRules.ScanVerificationVpsgBudgetMilliseconds);
            strictVpsgTuning.Normalize();
        }
        var vpsgTimer = Stopwatch.StartNew();
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "扫描 VPSG rescue 开始",
            details: new()
            {
                ["scan_vpsg_started"] = true,
                ["map"] = candidate.Map.DisplayName,
                ["mapId"] = candidate.Map.Id,
                ["floor"] = candidate.FloorKey,
                ["reason"] = string.Join(";", rejectionChain)
            });
        var vpsgAttempt = _recognition.AlignLockedFloorFeature(
            frame,
            candidate.Map.Id,
            candidate.FloorKey,
            templateSeed.LockedTransform,
            _settings!.OverlayAlignmentMode,
            alignmentTuning,
            strictVpsgTuning,
            candidate.MatchScore);
        vpsgTimer.Stop();
        if (isScanVerification && lastScanAttempt is not null)
            MergeScanVerificationCounters(
                vpsgAttempt.Diagnostics,
                lastScanAttempt.Diagnostics);
        PopulateScanAttemptTiming(
            vpsgAttempt,
            templateAttempt is null
                ? 0d
                : templateAttempt.Diagnostics.ScanTemplateValidationMilliseconds,
            vpsgTimer.Elapsed.TotalMilliseconds,
            vpsgAttempted: true);
        SetScaleSeedDiagnostics(
            vpsgAttempt,
            MapScaleSeedSource.Vpsg,
            vpsgAttempt.Diagnostics.ScaleBootstrapScale,
            cacheSeed?.SourceResolution,
            targetResolution,
            string.Join(";", rejectionChain),
            cacheSeed?.CacheSource.ToString() ?? string.Empty,
            false,
            0d);
        LogScaleSeedDecision(
            candidate,
            "vpsg",
            vpsgAttempt.Diagnostics.ScaleBootstrapScale,
            null,
            targetResolution,
            vpsgAttempt,
            string.Join(";", rejectionChain));
        var vpsgAdaptiveQualified = IsAdaptiveInitialScaleQualified(
            vpsgAttempt,
            structureTuning);
        var vpsgFormalAccepted = MapOpenAlignmentRouteRules
            .ShouldShortCircuitScanVerification(vpsgAttempt);
        LogScanVerificationStage(
            "scan_vpsg_formal",
            candidate,
            vpsgAttempt,
            vpsgAdaptiveQualified,
            shortCircuited: isScanVerification && vpsgFormalAccepted);
        if ((isScanVerification && vpsgFormalAccepted)
            || vpsgAdaptiveQualified)
            return vpsgAttempt;
        rejectionChain.Add($"vpsg:{DescribeAttemptFailure(vpsgAttempt)}");

        if (isScanVerification)
            return vpsgAttempt;

        var fallbackSeed = templateSeed;
        var vpsgScale = vpsgAttempt.Diagnostics.ScaleBootstrapScale;
        var hasVpsgScale = double.IsFinite(vpsgScale) && vpsgScale > 0.05d;
        if (hasVpsgScale)
        {
            // Preserve the content-derived scale even when its strict
            // validation is inconclusive. The observed side entrance keeps
            // the translation anchored while the normal side route performs
            // its unrestricted structure recovery around the VPSG scale.
            fallbackSeed = templateSeed.WithUniformScale(vpsgScale);
        }

        var finalTemplateAttempt = AlignSideEntranceFromSeed(
            frame,
            candidate,
            fallbackSeed,
            alignmentTuning,
            structureTuning);
        usedSeed = fallbackSeed;
        SetScaleSeedDiagnostics(
            finalTemplateAttempt,
            hasVpsgScale
                ? MapScaleSeedSource.Vpsg
                : MapScaleSeedSource.SideTemplate,
            fallbackSeed.LockedTransform.ScaleX,
            cacheSeed?.SourceResolution,
            targetResolution,
            string.Join(";", rejectionChain),
            cacheSeed?.CacheSource.ToString() ?? string.Empty,
            false,
            0d);
        LogScaleSeedDecision(
            candidate,
            hasVpsgScale ? "vpsg-global-recovery" : "side-template",
            fallbackSeed.LockedTransform.ScaleX,
            null,
            targetResolution,
            finalTemplateAttempt,
            string.Join(";", rejectionChain));
        return finalTemplateAttempt;
    }

    private static void MergeScanVerificationCounters(
        MapScanDiagnostics target,
        MapScanDiagnostics source)
    {
        target.ScanFormalStructureAttemptCount +=
            source.ScanFormalStructureAttemptCount;
        target.ScanShadowPairCount += source.ScanShadowPairCount;
        target.ScanShadowTrueFormalFalseCount +=
            source.ScanShadowTrueFormalFalseCount;
        target.ScanShadowFalseFormalTrueCount +=
            source.ScanShadowFalseFormalTrueCount;
        target.ScanShadowTrueFormalTrueCount +=
            source.ScanShadowTrueFormalTrueCount;
        target.ScanShadowFalseFormalFalseCount +=
            source.ScanShadowFalseFormalFalseCount;
    }

    private void LogScanVerificationStage(
        string stage,
        SideEntranceScanCandidate candidate,
        MapRecognitionAttempt? attempt,
        bool adaptiveQualified,
        bool shortCircuited)
    {
        var rawChamfer = SideEntranceCandidateEvidence.ResolveRawChamferPixels(
            attempt?.StructureResult);
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            stage,
            details: new()
            {
                ["map"] = candidate.Map.DisplayName,
                ["mapId"] = candidate.Map.Id,
                ["floor"] = candidate.FloorKey,
                ["structureAttempted"] = attempt?.StructureAttempted ?? false,
                ["structureAccepted"] = attempt?.StructureAccepted ?? false,
                ["hasRecognition"] = attempt?.Recognition is not null,
                ["confidence"] = attempt?.Recognition?.Result.Confidence,
                ["chamfer"] = double.IsFinite(rawChamfer)
                    ? rawChamfer
                    : null,
                ["candidateMargin"] = attempt?.Recognition?
                    .Result.StructureCandidateMargin
                    ?? attempt?.StructureResult?.CandidateMargin,
                ["adaptiveQualified"] = adaptiveQualified,
                ["shortCircuited"] = shortCircuited,
                ["failureReason"] = attempt?.FailureReason
            });
    }

    private static void PopulateScanAttemptTiming(
        MapRecognitionAttempt attempt,
        double templateMilliseconds,
        double vpsgMilliseconds,
        bool vpsgAttempted)
    {
        var diagnostics = attempt.Diagnostics;
        diagnostics.ScanTemplateValidationMilliseconds = templateMilliseconds;
        diagnostics.ScanVpsgMilliseconds = vpsgMilliseconds;
        diagnostics.ScanVpsgAttempted = vpsgAttempted;
        diagnostics.ScanFullRecoveryAttempted = false;
        diagnostics.ScanStructureMilliseconds =
            diagnostics.StructurePreprocessMilliseconds
            + diagnostics.StructureSearchMilliseconds
            + diagnostics.StructureRefineMilliseconds;
    }
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScaleSideEntrance。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
