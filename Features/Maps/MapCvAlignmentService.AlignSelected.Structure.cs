using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt AlignSelectedWithStructure(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        SelectedAlignmentRoute route,
        AlignmentSearchContext? searchCtx,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        IReadOnlyList<GateDetection> gates,
        GateDetectionResult gateResult,
        MapScanDiagnostics diagnostics,
        Mat reference,
        List<Rect> dynamicIgnoreRegions,
        string? singleGateFallbackReason,
        RuntimeMapRecognition? singleGateProposal,
        MapOverlayTransform? freshAnchorTransform,
        MapOverlayTransform structureSeed,
        Stopwatch stopwatch)
    {
        // 辅助锚点已停用：此路径不再执行辅助锚点追踪。
        // structureSeed / freshAnchorTransform 保持单门准备阶段传入的值，
        // 后续锚点冲突校验（AnchorTransformConflict）仍基于该单门结果。

        dynamicIgnoreRegions.AddRange(
            MapCvRecognitionBuilders.BuildProjectedOutsideIgnoreRegions(
                fingerprint.Map,
                fingerprint.FloorKey,
                frame,
                structureSeed));

        var primaryProfile = MapFloorRules.GetFloorProfile(
            fingerprint.Map,
            fingerprint.FloorKey)
            ?? fingerprint.Map.Recognition.FirstFloor;
        var vpsgMode = Enum.IsDefined(structureTuning.VpsgScaleMode)
            ? structureTuning.VpsgScaleMode
            : VpsgScaleMode.Structure;
        var structurePreprocessingProfile = route == SelectedAlignmentRoute.SideEntrance
            && vpsgMode == VpsgScaleMode.Structure
                ? MapStructurePreprocessingProfile.EdgesOnly
                : MapStructurePreprocessingProfile.EdgesAndFeatures;

        stopwatch.Restart();
        using var residentReferenceLease = service.StructureCache.TryRentResident(
            fingerprint.Map.Id,
            fingerprint.Map.UpdatedAt,
            fingerprint.FloorKey,
            structureTuning.Generation,
            structurePreprocessingProfile);
        MapStructureFeatures? ownedPreparedReference = null;
        if (residentReferenceLease is null)
        {
            ownedPreparedReference = service.StructureCache.GetOrCreate(
                fingerprint.Map.Id,
                fingerprint.Map.UpdatedAt,
                reference,
                primaryProfile.WholeImageIgnoreRegions,
                fingerprint.FloorKey,
                structureTuning.Generation,
                structurePreprocessingProfile);
        }
        using var ownedPreparedReferenceScope = ownedPreparedReference;
        var preparedReference = residentReferenceLease?.Features
            ?? ownedPreparedReference!;
        stopwatch.Stop();
        diagnostics.CacheMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
        diagnostics.ReferenceCacheMilliseconds +=
            stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        using var preparedLive =
            service.StructurePreprocessor.ProcessLiveRoiDiagnostic(
            frame.ComputationImage,
            liveIgnoreRegions,
            dynamicIgnoreRegions.Select(frame.ToComputationRect).ToArray(),
                out var liveStructureTiming,
                profile: structurePreprocessingProfile,
            generateVisibleMask: structureTuning.EnableVisibleMask,
            generationTuning: structureTuning.Generation);
        stopwatch.Stop();
        diagnostics.StructurePreprocessMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;
        diagnostics.LiveStructurePreprocessMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "侧门/锚点路线实时帧结构特征提取完成",
            elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            details: CreateLiveStructureLogDetails(
                frame,
                preparedLive,
                liveStructureTiming,
                "route-structure-extraction",
                stopwatch.Elapsed.TotalMilliseconds,
                stopwatch.Elapsed.TotalMilliseconds,
                diagnostics.ReferenceImageLoadMilliseconds,
                diagnostics.ReferenceCacheMilliseconds,
                liveIgnoreRegions?.Count ?? 0,
                dynamicIgnoreRegions.Count,
                route.ToString()));

        // 辅助锚点已停用，锚点种子仅来自单门提案。
        var hasAnchorSeed = singleGateProposal is not null;
        // A side-entrance scan seed already contains a same-frame feature
        // match, scale and translation.  Requiring the generic single-gate
        // classifier to identify that gate again discards the strongest scan
        // evidence on sparse screenshots and restarts structure registration
        // at scale 1.0.  Keep the scan seed as the restricted, scale-searching
        // structure prior; the registrar still has to accept independent wall
        // structure before the map is committed.
        var isSideEntranceStructureRoute = route == SelectedAlignmentRoute.SideEntrance
            && (singleGateProposal is not null
                || searchCtx?.UseRestrictedStructureFallback == true);
        var isScanVerification = structureTuning.Mode ==
            MapStructureRegistrationMode.ScanVerification;
        var isInitialSideEntranceSeed = isSideEntranceStructureRoute
            && searchCtx?.UseInitialHighPrecisionRecovery == true;
        if (route == SelectedAlignmentRoute.SideEntrance
            && singleGateProposal is null)
        {
            var fallbackKind = gates.Count switch
            {
                0 => "未检测到门",
                1 when !string.IsNullOrWhiteSpace(singleGateFallbackReason)
                    => "单门身份确认失败",
                1 => "单门未形成可靠侧门证据",
                _ => "检测到多扇门"
            };
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"侧门单门复核不可用，保留扫描种子进行结构验证 · {fallbackKind}",
                details: new()
                {
                    ["gateCount"] = gates.Count,
                    ["fallbackKind"] = fallbackKind,
                    ["singleGateFallbackReason"] = singleGateFallbackReason ?? string.Empty,
                    ["allowScaleSearch"] = isSideEntranceStructureRoute,
                    ["restrictSearchToLockedTransform"] = isSideEntranceStructureRoute
                });
        }
        var structureSearchTuning = structureTuning.Clone();
        if (isSideEntranceStructureRoute)
        {
            // The side-entrance seed already supplied independent local
            // features. Structure validation only needs edge geometry.
            structureSearchTuning.EnableFeatureVoting = false;
        }
        if (!isSideEntranceStructureRoute)
        {
            structureSearchTuning.TopCandidateCount = Math.Min(
                3,
                structureSearchTuning.TopCandidateCount);
        }

        // A locked side-feature observation already supplies a map-owned
        // transform proposal.  Even when it is a fixed-scale validation (and
        // therefore not the initial/recovery side route), keep the structure
        // search inside that proposal's local basin.
        var restrictStructureSearch = isSideEntranceStructureRoute
            || hasAnchorSeed
            || searchCtx?.UseLockedFixedStructureValidation == true;
        if (ApplyNoDoorBudgetBeforeLocalSearch(
                structureSearchTuning,
                isSideEntranceStructureRoute,
                diagnostics,
                gateResult) is { } budgetFailure)
        {
            return budgetFailure;
        }
        var structureRequest = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            Channel = structureSearchTuning.Channel,
            LiveRoi = frame.ComputationImage,
            OriginalLiveRoi = frame.Image,
            PhysicalPixelsPerLivePixel = frame.PhysicalPixelsPerComputationPixel,
            ViewportBounds = frame.ViewportBounds,
            LockedTransform = structureSeed,
            Tuning = structureSearchTuning,
            ScaleSearchPolicy = isScanVerification
                ? MapScaleSearchPolicy.Fixed
                : isSideEntranceStructureRoute
                    ? MapScaleSearchPolicy.Search
                : MapScaleSearchPolicy.Fixed,
            RestrictSearchToLockedTransform = isScanVerification
                || restrictStructureSearch,
            // 侧门初次配准的 seed 是扫描种子（不可靠），不应卡在 tracking 窄窗
            // （±0.5% scale / 48px）。非 tracking 改用 ScaleSearchRadius / 96px，
            // 给 seed 的尺度偏差更多纠正空间；非侧门路由仍保持 tracking。
            TrackingMode = !isScanVerification
                && MapAlignmentSearchPolicy.UseTrackingForStructureValidation(
                    isSideEntranceStructureRoute,
                    searchCtx),
            ForceBestCandidate = false,
            PreparedReference = preparedReference,
            PreparedLive = preparedLive,
            FixedRotationDegrees = primaryProfile.OrientationDegrees,
            ValidMapBounds = primaryProfile.GetEffectiveValidMapBounds(),
            PlayerPrior = playerPrior,
            PredictedViewportOrigin = predictedViewportOrigin,
            LiveIgnoreRegions = liveIgnoreRegions ?? [],
            DynamicIgnoreRegions = dynamicIgnoreRegions,
            CandidateHistory = candidateHistory ?? [],
            SideEntrancePrior = 0d
        };
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"侧门结构验证路线 · {(isScanVerification
                ? "scan-verification"
                : isInitialSideEntranceSeed
                    ? "initial-seed"
                : isSideEntranceStructureRoute
                    ? "tracking-repair"
                    : route == SelectedAlignmentRoute.SideEntrance
                        ? searchCtx?.UseLockedFixedStructureValidation == true
                            ? "locked-fixed"
                            : "standard"
                        : "standard")}",
            details: new()
            {
                ["route"] = isScanVerification
                    ? "scan-verification"
                    : isInitialSideEntranceSeed
                        ? "initial-seed"
                    : isSideEntranceStructureRoute
                        ? "tracking-repair"
                        : route == SelectedAlignmentRoute.SideEntrance
                            ? searchCtx?.UseLockedFixedStructureValidation == true
                                ? "locked-fixed"
                                : "standard"
                            : "standard",
                ["scaleSearchPolicy"] = structureRequest.ScaleSearchPolicy.ToString(),
                ["trackingMode"] = structureRequest.TrackingMode,
                ["restrictedSearch"] = structureRequest.RestrictSearchToLockedTransform
            });
        var scanCheapRejectWouldReject = false;
        var scanCheapRejectMilliseconds = 0d;
        var scanCheapRejectReason = string.Empty;
        if (isScanVerification
            && MapStructureCheapReject.TryReject(
                structureRequest,
                preparedReference,
                preparedLive,
                out scanCheapRejectMilliseconds,
                out scanCheapRejectReason))
        {
            scanCheapRejectWouldReject = true;
        }
        if (isScanVerification)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "扫描结构 cheap reject shadow",
                elapsedMs: scanCheapRejectMilliseconds,
                details: new()
                {
                    ["route"] = "scan-verification",
                    ["shadow"] = true,
                    ["wouldReject"] = scanCheapRejectWouldReject,
                    ["enforced"] = structureSearchTuning.EnableScanCheapReject,
                    ["cheapRejectMs"] = scanCheapRejectMilliseconds,
                    ["reason"] = scanCheapRejectReason
                });
        }
        if (scanCheapRejectWouldReject
            && structureSearchTuning.EnableScanCheapReject)
        {
            diagnostics.ScanCheapRejected = true;
            diagnostics.ScanCheapRejectMilliseconds = scanCheapRejectMilliseconds;
            diagnostics.ScanCheapRejectCount = 1;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureRejectionReason =
                MapStructureRejectionReason.WeakAbsoluteScore;
            diagnostics.StructureDisposition =
                MapStructureEvidenceDisposition.Inconclusive;
            var rejected = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.WeakAbsoluteScore,
                scanCheapRejectReason,
                preprocessMilliseconds: diagnostics.StructurePreprocessMilliseconds,
                searchMilliseconds: scanCheapRejectMilliseconds,
                lockedScale: structureRequest.LockedTransform.ScaleX,
                referenceWidth: preparedReference.Edges.Width,
                referenceHeight: preparedReference.Edges.Height,
                usedRestrictedSearch: structureRequest.RestrictSearchToLockedTransform);
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "扫描结构 cheap reject",
                elapsedMs: scanCheapRejectMilliseconds,
                details: new()
                {
                    ["route"] = "scan-verification",
                    ["cheapReject"] = true,
                    ["cheapRejectMs"] = scanCheapRejectMilliseconds,
                    ["reason"] = scanCheapRejectReason
                });
            return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                diagnostics,
                rejected,
                scanCheapRejectReason,
                gateResult,
                AlignmentSearchStage.StructureFallback);
        }
        if (isScanVerification)
            diagnostics.ScanFormalStructureAttemptCount++;
        var structure = service.StructureRegistrar.Register(structureRequest);
        if (isScanVerification)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "扫描结构 cheap reject shadow 对照",
                details: new()
                {
                    ["route"] = "scan-verification",
                    ["shadowWouldReject"] = scanCheapRejectWouldReject,
                    ["formalAccepted"] = structure.Accepted,
                    ["formalRejection"] = structure.RejectionReason.ToString(),
                    ["formalBestScore"] = structure.BestScore,
                    ["formalConfidence"] = structure.Confidence
                });
        }
        if (isSideEntranceStructureRoute
            && restrictStructureSearch
            && !isScanVerification
            && MapOpenAlignmentRouteRules.ShouldAttemptSideEntranceGlobalRecovery(
                isInitialSideEntranceSeed,
                structure.Accepted,
                structure.Confidence))
        {
            // A global recovery is a new identity search, so start from the
            // caller's complete tuning instead of inheriting local-search
            // mutations. Initial identity callers cap both normal and
            // restricted Chamfer ceilings at 3 px, so switching search modes
            // cannot weaken acceptance.
            var globalRecoveryTuning = structureTuning.Clone();
            globalRecoveryTuning.EnableFeatureVoting =
                structureSearchTuning.EnableFeatureVoting;
            if (TryApplyNoDoorBudgetBeforeGlobalSearch(
                    globalRecoveryTuning,
                    structure))
            {
                var globalRecoveryRequest = new MapStructureRegistrationRequest
                {
                    ReferenceImage = reference,
                    Channel = globalRecoveryTuning.Channel,
                    LiveRoi = frame.ComputationImage,
                    OriginalLiveRoi = frame.Image,
                    PhysicalPixelsPerLivePixel =
                        frame.PhysicalPixelsPerComputationPixel,
                    ViewportBounds = frame.ViewportBounds,
                    LockedTransform = structureSeed,
                    Tuning = globalRecoveryTuning,
                    ScaleSearchPolicy = MapScaleSearchPolicy.Search,
                    RestrictSearchToLockedTransform = false,
                    TrackingMode =
                        MapAlignmentSearchPolicy.UseTrackingForGlobalRecovery(
                            searchCtx),
                    ForceBestCandidate = false,
                    PreparedReference = preparedReference,
                    PreparedLive = preparedLive,
                    FixedRotationDegrees = primaryProfile.OrientationDegrees,
                    ValidMapBounds = primaryProfile.GetEffectiveValidMapBounds(),
                    PlayerPrior = playerPrior,
                    PredictedViewportOrigin = predictedViewportOrigin,
                    LiveIgnoreRegions = liveIgnoreRegions ?? [],
                    DynamicIgnoreRegions = dynamicIgnoreRegions,
                    CandidateHistory = candidateHistory ?? [],
                    SideEntrancePrior = 0d
                };
                var globalRecovery = service.StructureRegistrar.Register(
                    globalRecoveryRequest);
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "侧门结构验证路线 · global-recovery",
                    details: new()
                    {
                        ["route"] = "global-recovery",
                        ["scaleSearchPolicy"] = globalRecoveryRequest.ScaleSearchPolicy.ToString(),
                        ["trackingMode"] = globalRecoveryRequest.TrackingMode,
                        ["restrictedSearch"] = false
                    });
                if (globalRecovery.Accepted
                    || (!structure.Accepted
                        && (globalRecovery.Confidence > structure.Confidence
                            || globalRecovery.BestScore < structure.BestScore)))
                {
                    MapLogCollector.Instance.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Info,
                        "侧门结构局部搜索未通过，已尝试全局恢复",
                        details: new()
                        {
                            ["localAccepted"] = structure.Accepted,
                            ["localBestScore"] = structure.BestScore,
                            ["globalAccepted"] = globalRecovery.Accepted,
                            ["globalBestScore"] = globalRecovery.BestScore,
                            ["globalConfidence"] = globalRecovery.Confidence
                        });
                    structure = globalRecovery;
                }
            }
        }

        Debug.Assert(
            !isScanVerification || !diagnostics.ScanFullRecoveryAttempted);

        MapCvRecognitionDiagnostics.WriteStructureDebugResult(
            fingerprint.Map,
            structure,
            singleGateFallbackReason);

        diagnostics.StructureSearchMilliseconds =
            structure.SearchMilliseconds;
        diagnostics.StructureRefineMilliseconds =
            structure.RefineMilliseconds;
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence =
            MapAlignmentEvidenceKind.Structure;
        PopulateStructureDiagnostics(diagnostics, structure);

        var effectiveStructureConfidence = structure.Confidence;

        var postStructureTimer = Stopwatch.StartNew();
        if (!structure.Accepted
            || structure.Transform is null
            || (effectiveStructureConfidence < tuning.MinimumConfidence
                && !tuning.ForceBestRecognitionResult))
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            if (tuning.ForceBestRecognitionResult)
            {
                diagnostics.UsedForcedBestResult = true;
                diagnostics.StructureAttempted = true;
                diagnostics.StructureAccepted = false;
                diagnostics.StructureFailureReason =
                    structure.FailureReason;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    StructureResult = structure,
                    Recognition = MapCvRecognitionBuilders
                        .BuildReusedTransformRecognition(
                            fingerprint,
                            session,
                            structure),
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                    StructureAttempted = true,
                    StructureAccepted = false,
                    StructureFailureReason = structure.FailureReason,
                };
            }

            var failureReason = structure.Accepted
                && structure.Confidence < tuning.MinimumConfidence
                    ? $"结构配准置信度 {structure.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}"
                    : structure.FailureReason;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = failureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason =
                    (singleGateFallbackReason is null
                        ? string.Empty
                        : $"{singleGateFallbackReason}；已回退结构配准，但")
                    + $"{failureReason}；已保留最后可靠对齐，等待下次开图恢复。",
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = failureReason,
            };
        }

        var gateBaseline = session.BaselineGateScale > 0d
            ? session.BaselineGateScale
            : session.LockedTransform.ScaleX;
        if (Math.Abs((structure.Transform.ScaleX / gateBaseline) - 1d)
                > structureTuning.ScaleSearchRadius + 0.0001d
            && !tuning.ForceBestRecognitionResult)
        {
            var rejected = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.ScaleChangeTooLarge,
                candidates: structure.Candidates,
                preprocessMilliseconds: structure.PreprocessMilliseconds,
                searchMilliseconds: structure.SearchMilliseconds,
                debugOutputDirectory: structure.DebugOutputDirectory);
            return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                diagnostics,
                rejected,
                $"{rejected.FailureReason}；已保留最后可靠对齐，等待双门重新锁定。",
                gateResult,
                diagnostics.SearchStage);
        }

        if (isSideEntranceStructureRoute && freshAnchorTransform is not null)
        {
            var maxDeviation = Math.Max(
                Math.Abs(
                    structure.Transform.OffsetX
                    - freshAnchorTransform.OffsetX),
                Math.Abs(
                    structure.Transform.OffsetY
                    - freshAnchorTransform.OffsetY));
            if (maxDeviation > MapCvRecognitionService.SideEntranceAnchorDeviationTolerancePixels)
            {
                var rejected = MapStructureRegistrationResult.Reject(
                    MapStructureRejectionReason.AnchorTransformConflict,
                    candidates: structure.Candidates,
                    preprocessMilliseconds: structure.PreprocessMilliseconds,
                    searchMilliseconds: structure.SearchMilliseconds,
                    debugOutputDirectory: structure.DebugOutputDirectory);
                return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                    diagnostics,
                    rejected,
                    $"{rejected.FailureReason}；结构结果与本次锚点位置偏差超过 "
                    + $"{MapCvRecognitionService.SideEntranceAnchorDeviationTolerancePixels:F0}px，已拒绝。",
                    gateResult,
                    diagnostics.SearchStage);
            }
        }

        diagnostics.TrackingMode = isSideEntranceStructureRoute
            ? MapAlignmentTrackingMode.StructureMatched
            : singleGateProposal is null
                ? MapAlignmentTrackingMode.StructureMatched
                : MapAlignmentTrackingMode.SingleGateTracking;
        diagnostics.UsedForcedBestResult =
            tuning.ForceBestRecognitionResult
            && (structure.WasForcedBestCandidate
                || structure.Confidence < tuning.MinimumConfidence);
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = structure.Accepted;
        diagnostics.StructureFailureReason =
            structure.Accepted ? string.Empty : structure.FailureReason;

        postStructureTimer.Stop();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构后处理完成 · {postStructureTimer.Elapsed.TotalMilliseconds:F0}ms",
            elapsedMs: postStructureTimer.Elapsed.TotalMilliseconds);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = MapCvRecognitionBuilders.BuildStructureRecognition(
                fingerprint,
                structure.Transform,
                structure,
                diagnostics.UsedForcedBestResult,
                singleGateProposal,
                confidenceOverride: null),
            GateDetectionResult = gateResult,
            SearchStage = diagnostics.SearchStage,
            StructureAttempted = true,
            StructureAccepted = structure.Accepted,
            StructureFailureReason =
                structure.Accepted ? string.Empty : structure.FailureReason,
        };
    }

}
/*
 * 文件职责：MapCvAlignmentService.AlignSelected.Structure。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
