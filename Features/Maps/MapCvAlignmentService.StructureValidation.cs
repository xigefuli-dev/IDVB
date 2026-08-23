using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    internal static bool TryValidateAnchorRecognitionWithStructure(
        MapCvRecognitionService service,
        MapGeometryFingerprint fingerprint,
        CapturedGameFrame frame,
        RuntimeMapRecognition anchorRecognition,
        MapStructureRegistrationTuning structureTuning,
        double minimumConfidence,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<Rect> dynamicIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        out RuntimeMapRecognition validatedRecognition,
        out MapStructureRegistrationResult? structure,
        out string failureReason)
    {
        validatedRecognition = anchorRecognition;
        structure = null;
        failureReason = string.Empty;
        if (anchorRecognition.Result.OverlayTransform
            is not { } anchorTransform)
        {
            failureReason = "双门结果没有有限的相似变换。";
            return false;
        }

        using var reference = Cv2.ImRead(
            fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
        if (reference.Empty())
        {
            failureReason = "无法读取当前地图的主层识别图。";
            return false;
        }

        using var preparedReference = service.StructureCache.GetOrCreate(
            fingerprint.Map.Id,
            fingerprint.Map.UpdatedAt,
            reference,
                (MapFloorRules.GetFloorProfile(
                fingerprint.Map,
                fingerprint.FloorKey)
                ?? fingerprint.Map.Recognition.FirstFloor)
                .WholeImageIgnoreRegions,
            fingerprint.FloorKey,
            structureTuning.Generation);

        var effectiveDynamicIgnoreRegions = dynamicIgnoreRegions
            .Concat(MapCvRecognitionBuilders.BuildProjectedOutsideIgnoreRegions(
                fingerprint,
                frame,
                anchorTransform))
            .Distinct()
            .ToArray();

        MapStructureFeatures? preparedLive = null;
        try
        {
            preparedLive = service.StructurePreprocessor.ProcessLiveRoi(
                frame.Image,
                liveIgnoreRegions,
                effectiveDynamicIgnoreRegions,
                generateVisibleMask: structureTuning.EnableVisibleMask,
                generationTuning: structureTuning.Generation);
        }
        catch (Exception preprocessEx)
        {
            // 结构预处理异常时接受几何结果（结构配准是优化，非必须）
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                $"结构预处理跳过，使用双门几何结果：{preprocessEx.Message}");
            validatedRecognition = anchorRecognition;
            structure = null;
            failureReason = string.Empty;
            return true;
        }

        using var _preparedLiveDispose = preparedLive;

        var validationTuning = structureTuning.Clone();
        validationTuning.ScaleSearchRadius = 0d;
        validationTuning.TopCandidateCount = Math.Min(
            5,
            validationTuning.TopCandidateCount);
        validationTuning.PreviousAlignmentSearchRadiusPixels = Math.Max(
            8,
            (int)Math.Ceiling(8d * anchorTransform.ScaleX));

        try
        {
            structure = service.StructureRegistrar.Register(
                new MapStructureRegistrationRequest
                {
                    ReferenceImage = reference,
                    Channel = validationTuning.Channel,
                    LiveRoi = frame.Image,
                    ViewportBounds = frame.ViewportBounds,
                    LockedTransform = anchorTransform,
                    Tuning = validationTuning,
                    ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
                    RestrictSearchToLockedTransform = true,
                    ForceBestCandidate = false,
                    PreparedReference = preparedReference,
                    PreparedLive = preparedLive,
                    FixedRotationDegrees = (MapFloorRules.GetFloorProfile(
                        fingerprint.Map,
                        fingerprint.FloorKey)
                        ?? fingerprint.Map.Recognition.FirstFloor)
                        .OrientationDegrees,
                    ValidMapBounds = (MapFloorRules.GetFloorProfile(
                        fingerprint.Map,
                        fingerprint.FloorKey)
                        ?? fingerprint.Map.Recognition.FirstFloor)
                        .GetEffectiveValidMapBounds(),
                    PlayerPrior = playerPrior,
                    PredictedViewportOrigin = predictedViewportOrigin,
                    LiveIgnoreRegions = liveIgnoreRegions ?? [],
                    DynamicIgnoreRegions = effectiveDynamicIgnoreRegions,
                    CandidateHistory = candidateHistory ?? [],
                    SideEntrancePrior = 0d
                });
        }
        catch (Exception registerEx)
        {
            // 结构配准异常时接受几何结果（结构配准是优化，非必须）
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                $"结构配准跳过，使用双门几何结果：{registerEx.Message}");
            validatedRecognition = anchorRecognition;
            structure = null;
            failureReason = string.Empty;
            return true;
        }

        if (!structure.Accepted
            || structure.Transform is null
            || structure.Confidence < minimumConfidence)
        {
            failureReason = structure.Accepted
                ? $"结构置信度 {structure.Confidence:P0} 低于 {minimumConfidence:P0}。"
                : structure.FailureReason;
            return false;
        }

        var structureTransform = structure.Transform;
        var maximumFineCorrection = Math.Max(
            3d,
            validationTuning.PreviousAlignmentSearchRadiusPixels);
        if (Math.Abs(
                structureTransform.OffsetX
                - anchorTransform.OffsetX) > maximumFineCorrection
            || Math.Abs(
                structureTransform.OffsetY
                - anchorTransform.OffsetY) > maximumFineCorrection
            || Math.Abs(
                (structureTransform.ScaleX / anchorTransform.ScaleX)
                - 1d) > 0.003d)
        {
            failureReason =
                "结构精修超出双门候选允许的局部平移范围。";
            structure = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.AnchorTransformConflict,
                failureReason,
                structure.Candidates,
                structure.PreprocessMilliseconds,
                structure.SearchMilliseconds,
                structure.DebugOutputDirectory,
                structure.LockedScale,
                structure.ReferenceWidth,
                structure.ReferenceHeight,
                structure.QueryEdgePixels,
                new Rect(
                    structure.QueryBoundsX,
                    structure.QueryBoundsY,
                    structure.QueryBoundsWidth,
                    structure.QueryBoundsHeight),
                structure.ScaleHypothesisCount,
                structure.OversizedHypothesisCount,
                structure.UsedRestrictedSearch);
            return false;
        }

        var confidence = new MapRegistrationConfidenceEvidence
        {
            AnchorGeometry = anchorRecognition.Result.Confidence,
            StructureQuality = structure.Confidence,
            CandidateSeparation = structure.CandidateMargin,
            BoundsAndPrior = 1d
        }.Calculate();

        validatedRecognition = new RuntimeMapRecognition
        {
            Map = anchorRecognition.Map,
            FloorImagePath = anchorRecognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = anchorRecognition.Result.MapId,
                Floor = anchorRecognition.Result.Floor,
                OrientationDegrees =
                    anchorRecognition.Result.OrientationDegrees,
                Confidence = confidence,
                IdentityConfidence =
                    anchorRecognition.Result.IdentityConfidence,
                LocalizationConfidence = confidence,
                Source = anchorRecognition.Result.Source,
                HasAllRequiredAnchorEvidence =
                    anchorRecognition.Result.HasAllRequiredAnchorEvidence,
                GeometryMargin =
                    anchorRecognition.Result.GeometryMargin,
                UsedLocalConfirmation = true,
                OverlayTransform = structureTransform,
                AnchorMatches =
                    anchorRecognition.Result.AnchorMatches,
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin =
                    structure.CandidateMargin,
                StructureRejectionReason =
                    structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    MapStructureEvidenceDisposition.Supportive,
                WasForcedBestResult = false
            }
        };
        return true;
    }

    internal static void PopulateStructureDiagnostics(
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult structure)
    {
        diagnostics.StructureCandidateCount =
            structure.Candidates.Count;
        diagnostics.ScaleHypothesisCount = structure.ScaleHypothesisCount;
        diagnostics.CoarseGlobalMilliseconds =
            structure.FastCoarseSearchMilliseconds;
        diagnostics.PyramidRefineMilliseconds = structure.RefineMilliseconds;
        diagnostics.ExactEvaluateMilliseconds = structure.CandidateRankingMilliseconds;
        diagnostics.UmatAttempted =
            string.Equals(structure.VisibleAwareRequestedBackend, "UMat",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(structure.VisibleAwareActualBackend, "UMat",
                StringComparison.OrdinalIgnoreCase);
        diagnostics.StructureFeatureMatchCount =
            structure.FeatureMatchCount;
        diagnostics.StructureFeatureInlierCount =
            structure.FeatureInlierCount;
        diagnostics.StructureFeatureConsensus =
            structure.FeatureConsensus;
        diagnostics.StructureEccConverged =
            structure.EccConverged;
        diagnostics.StructureEccCorrelation =
            structure.EccCorrelation;
        if (structure.ConfidenceBreakdown is { } breakdown)
        {
            diagnostics.StructureGeometricFitQuality =
                breakdown.GeometricFitQuality;
            diagnostics.StructureEvidenceConfidence =
                breakdown.EvidenceConfidence;
            diagnostics.StructureGeometricLockConfidence =
                breakdown.GeometricLockConfidence;
            diagnostics.StructureLockConfidence =
                breakdown.LockConfidence;
            diagnostics.StructureLowEvidenceReason =
                breakdown.LowEvidenceReason;
            diagnostics.StructureHardGateFailure =
                breakdown.HardGateFailure;
        }

        diagnostics.VisibleMaskMs =
            structure.VisibleMaskMilliseconds;
        diagnostics.VisibleFraction =
            structure.VisibleFraction;
        diagnostics.VisibleStructurePixels =
            structure.VisibleStructurePixels;
        diagnostics.VisibleEdgePixels =
            structure.VisibleEdgePixels;
        diagnostics.VisibleAwareSearchMs =
            structure.VisibleAwareSearchMilliseconds;
        diagnostics.VisibleAwareCandidateCount =
            structure.VisibleAwareCandidateCount;
        diagnostics.VisibleAwareTopCost =
            structure.VisibleAwareTopCost;
        diagnostics.VisibleAwareTopMargin =
            structure.VisibleAwareTopMargin;
        diagnostics.VisibleAwareEarlyAccepted =
            structure.VisibleAwareEarlyAccepted;
        diagnostics.VisibleAwareFallbackReason =
            structure.VisibleAwareFallbackReason;
        diagnostics.VisibleAwareRequestedBackend = structure.VisibleAwareRequestedBackend;
        diagnostics.VisibleAwareActualBackend = structure.VisibleAwareActualBackend;
        diagnostics.VisibleAwareUMatFallbackReason = structure.VisibleAwareUMatFallbackReason;
        diagnostics.VisibleAwareCoarseMs = structure.VisibleAwareCoarseMilliseconds;
        diagnostics.VisibleAwareRefineMs = structure.VisibleAwareRefineMilliseconds;
        diagnostics.VisibleAwareUploadMs = structure.VisibleAwareUploadMilliseconds;
        diagnostics.VisibleAwareDownloadMs = structure.VisibleAwareDownloadMilliseconds;
        diagnostics.VisibleAwareCompletedScaleCount = structure.VisibleAwareCompletedScaleCount;
        diagnostics.VisibleAwareBudgetSkippedScaleCount = structure.VisibleAwareBudgetSkippedScaleCount;
        diagnostics.VisibleAwareCoarsePeakCount = structure.VisibleAwareCoarsePeakCount;
        diagnostics.VisibleAwareRefinedCandidateCount = structure.VisibleAwareRefinedCandidateCount;
        diagnostics.StructureFastStrategyUsed =
            structure.UsedFastStrategy;
        diagnostics.StructureCoarseSearchMs =
            structure.FastCoarseSearchMilliseconds;
        diagnostics.StructureCoarseCandidateCount =
            structure.FastCoarseCandidateCount;
    }
}
/*
 * 文件职责：MapCvAlignmentService.StructureValidation。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
