using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    /// <summary>
    /// Side-entrance tracking uses one identified gate when possible and falls
    /// back to static structure. It never ranks or commits a dual-gate pair.
    /// </summary>
    public MapRecognitionAttempt AlignSideEntrance(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        AlignmentSearchContext? alignmentSearchContext = null,
        double nativeScaleChangeRatio = MapSessionRules.NativeScaleChangeRatio,
        string? mapClass = null)
    {
        var selectedMap = TryGetMap(selectedMapId);
        if (selectedMap is not null
            && MapFloorRules.GetFloorProfile(selectedMap, session.FloorKey)
                is not null
            && MapAlignmentChannelRegistry.Resolve(
                selectedMap,
                session.FloorKey).Channel == MapAlignmentChannel.LowStructure)
        {
            // A direct caller may still carry a side-entrance session from a
            // different route. The exact floor marker is authoritative:
            // low-structure floors have no doors, so do not even construct a
            // side-door search context before entering structure alignment.
            var lowStructureTuning = structureTuning?.Clone()
                ?? MapAlignmentChannelRegistry.CreateLowStructure();
            lowStructureTuning.Channel = MapAlignmentChannel.LowStructure;
            lowStructureTuning.EnableFeatureVoting = false;
            lowStructureTuning.LowStructureEnableFeatureScaleEstimate = false;
            lowStructureTuning.Normalize();
            return MapCvAlignmentService.AlignStructureOnly(
                this,
                frame,
                selectedMapId,
                session.FloorKey,
                MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                    selectedMap,
                    session.FloorKey),
                alignmentMode,
                tuning,
                lowStructureTuning,
                playerPrior,
                predictedViewportOrigin,
                liveIgnoreRegions,
                candidateHistory,
                isTracking: false,
                useProjectedBoundaryMask: false,
                allowPrimaryFloor: true,
                scaleSearchPolicy: MapScaleSearchPolicy.Search,
                identityPriorConfidence: session.LastConfidence,
                restrictTranslationToSeed: false);
        }

        // The selected-map confirmation path already supplies a warm-search
        // context.  Re-open alignment used to omit it, which silently changed
        // every later side-entrance alignment into a FullSearch.  Reconstruct
        // the same narrow gate-scale prior here so all callers keep the side
        // route semantics.
        alignmentSearchContext ??= CreateSideEntranceWarmSearchContext(
            session,
            tuning);

        return MapCvAlignmentService.AlignSelectedCore(
            this,
            frame,
            selectedMapId,
            session,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            alignmentSearchContext,
            nativeScaleChangeRatio,
            mapClass,
            SelectedAlignmentRoute.SideEntrance);
    }

    private static AlignmentSearchContext? CreateSideEntranceWarmSearchContext(
        MapAlignmentSession session,
        MapRecognitionTuning tuning,
        bool useInitialHighPrecisionRecovery = false,
        bool useLockedFixedStructureValidation = false)
    {
        var warmScale = session.GateTemplateScale
            ?? (GateTemplateRules.ReferenceScale * session.BaselineGateScale);
        if (!double.IsFinite(warmScale) || warmScale <= 0d)
            return null;

        var context = new AlignmentSearchContext
        {
            UseRestrictedStructureFallback =
                useInitialHighPrecisionRecovery,
            UseInitialHighPrecisionRecovery =
                useInitialHighPrecisionRecovery,
            UseLockedFixedStructureValidation =
                useLockedFixedStructureValidation,
            GateSearch = new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = warmScale,
                AllowDualGateEarlyExit = !useInitialHighPrecisionRecovery,
                AllowSingleGateEarlyExit = !useInitialHighPrecisionRecovery,
                SingleGateScoreThreshold = GateTemplateRules.EarlyExitScoreThreshold,
                SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
                AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
            }
        };
        if (tuning.WarmGateSearchBudgetMs > 0)
            context.GateSearch.TimeBudgetMilliseconds =
                tuning.WarmGateSearchBudgetMs;
        return context;
    }

    /// <summary>
    /// Aligns one exact non-primary floor from its own static structure. This
    /// path never calls gate or auxiliary-anchor detection and never inherits
    /// translation from another floor.
    /// </summary>
    public MapRecognitionAttempt AlignFloorWithoutGates(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        bool isTracking = false,
        bool useProjectedBoundaryMask = false,
        MapScaleSearchPolicy scaleSearchPolicy = MapScaleSearchPolicy.Search,
        double identityPriorConfidence = 0d,
        bool allowPrimaryFloor = false) =>
        MapCvAlignmentService.AlignStructureOnly(
            this,
            frame,
            selectedMapId,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            isTracking,
            useProjectedBoundaryMask,
            allowPrimaryFloor,
            scaleSearchPolicy,
            identityPriorConfidence,
            restrictTranslationToSeed: true);

    internal MapRecognitionAttempt AlignFloorWithoutGates(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        bool isTracking,
        bool useProjectedBoundaryMask,
        MapScaleSearchPolicy scaleSearchPolicy,
        double identityPriorConfidence,
        bool allowPrimaryFloor,
        LowStructureAlignmentPlan lowStructurePlan) =>
        MapCvAlignmentService.AlignStructureOnly(
            this,
            frame,
            selectedMapId,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            isTracking,
            useProjectedBoundaryMask,
            allowPrimaryFloor,
            scaleSearchPolicy,
            identityPriorConfidence,
            restrictTranslationToSeed: true,
            lowStructurePlan);

    public MapRecognitionAttempt AlignWithCachedScale(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        double identityPriorConfidence = 0d,
        bool restrictTranslationToSeed = true) =>
        MapCvAlignmentService.AlignStructureOnly(
            this,
            frame,
            selectedMapId,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior: null,
            predictedViewportOrigin: null,
            liveIgnoreRegions: null,
            candidateHistory: null,
            isTracking: false,
            useProjectedBoundaryMask: false,
            allowPrimaryFloor: true,
            scaleSearchPolicy: MapScaleSearchPolicy.Fixed,
            identityPriorConfidence,
            restrictTranslationToSeed);

    internal MapRecognitionAttempt AlignWithCachedScale(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning,
        double identityPriorConfidence,
        bool restrictTranslationToSeed,
        LowStructureAlignmentPlan lowStructurePlan) =>
        MapCvAlignmentService.AlignStructureOnly(
            this,
            frame,
            selectedMapId,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior: null,
            predictedViewportOrigin: null,
            liveIgnoreRegions: null,
            candidateHistory: null,
            isTracking: false,
            useProjectedBoundaryMask: false,
            allowPrimaryFloor: true,
            scaleSearchPolicy: MapScaleSearchPolicy.Fixed,
            identityPriorConfidence,
            restrictTranslationToSeed,
            lowStructurePlan);

    /// <summary>
    /// Thin wrapper that reuses AlignSelected for confirmation frames.
    /// Uses local ROI search around predicted gate positions and
    /// restricted structure fallback — never upgrades to FullSearch.
    /// </summary>
    public MapRecognitionAttempt ConfirmSelectedAlignment(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapRecognitionAttempt previousAttempt,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        double nativeScaleChangeRatio = MapSessionRules.NativeScaleChangeRatio,
        string? mapClass = null) =>
        MapCvAlignmentService.ConfirmSelectedAlignment(
            this,
            frame,
            selectedMapId,
            session,
            previousAttempt,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            nativeScaleChangeRatio,
            mapClass);

    public MapRecognitionAttempt RecognizeManual(
        MapScreenRect viewportBounds,
        MapScreenRect mainGateBounds,
        MapScreenRect sideGateBounds,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);
        diagnostics.GateCandidateCount = 2;
        var fingerprints = FilterFingerprints(mapClass);
        if (fingerprints.Count == 0)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "没有已完成主层区域、大门和侧门标记的地图。");
        if (!viewportBounds.IsValid || !mainGateBounds.IsValid || !sideGateBounds.IsValid)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "手动框选的地图区域或门矩形无效。");

        var gates = new[]
        {
            new GateDetection
            {
                Score = 1d,
                Scale = 0d,
                ScreenBounds = mainGateBounds
            },
            new GateDetection
            {
                Score = 1d,
                Scale = 0d,
                ScreenBounds = sideGateBounds
            }
        };
        var stopwatch = Stopwatch.StartNew();
        var ranked = MapCvRecognitionScript.RankGeometry(
            fingerprints,
            gates,
            viewportBounds,
            tuning.VectorErrorTolerance,
            testSwappedAssignments: false);
        stopwatch.Stop();
        diagnostics.GeometryMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        if (!MapCvRecognitionDiagnostics.TryValidateRanking(
                ranked, tuning, diagnostics, out var failure))
        {
            // 即使不满足排名门槛，玩家手动框了门就值得展示候选供选择
            var rescueChoices = MapCvRecognitionBuilders.BuildChoices(
                ranked, alignmentMode, tuning, double.PositiveInfinity,
                MapRecognitionSource.ManualGateSelection);
            if (rescueChoices.Count > 0)
            {
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Choices = rescueChoices,
                    FailureReason = failure!.FailureReason + " 请从候选中选择。"
                };
            }
            return failure!;
        }

        var margin = MapCvRecognitionHelpers.GeometryMargin(ranked);
        var choices = MapCvRecognitionBuilders.BuildChoices(
            ranked, alignmentMode, tuning, margin,
            MapRecognitionSource.ManualGateSelection);
        if (choices.Count == 0)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "手动双门坐标无法生成安全的无旋转缩放与位移。");

        diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
        var winner = choices[0].Recognition;
        if (!tuning.ForceCandidateSelection
            && margin >= tuning.AmbiguityMargin
            && winner.Result.Confidence >= tuning.MinimumConfidence)
        {
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = winner
            };
        }
        if (!tuning.ForceCandidateSelection
            && tuning.ForceBestRecognitionResult)
        {
            diagnostics.UsedForcedBestResult = true;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = MapCvRecognitionBuilders.MarkForcedBestResult(winner)
            };
        }

        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Choices = choices,
            FailureReason = tuning.ForceCandidateSelection
                ? "强制候选模式已开启，请选择正确地图。"
                : winner.Result.Confidence < tuning.MinimumConfidence
                    ? $"最高置信度 {winner.Result.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}，请选择正确地图。"
                    : "前几名地图过于接近，请选择正确地图。"
        };
    }

}
/*
 * 文件职责：MapCvRecognitionService.Alignment。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
