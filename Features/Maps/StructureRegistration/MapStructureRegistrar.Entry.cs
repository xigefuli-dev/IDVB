using IDVBuff.Pipeline;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    private readonly MapStructurePreprocessor _preprocessor;
    private readonly object _registrationGate = new();

    /// <summary>
    /// When baseline scale is below one, downsample the reference instead of
    /// enlarging the query so its edge geometry stays sharp.
    /// </summary>
    private ReciprocalScaleContext _currentReciprocalScale =
        ReciprocalScaleContext.None;

    internal sealed class ReciprocalScaleContext
    {
        public double ReferenceScale { get; init; } = 1d;
        public Mat? Edges { get; init; }
        public Mat? StructureMask { get; init; }
        public static readonly ReciprocalScaleContext None = new();
    }

    public MapStructureRegistrar(MapStructurePreprocessor preprocessor)
    {
        _preprocessor = preprocessor;
    }

    public MapStructureRegistrationResult Register(
        MapStructureRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var registration = MapOperationTraceAmbient.StartChild(
            "structure_registration",
            MapOperationWaitKind.Compute);
        lock (_registrationGate)
        {
            using var inferredComputationRoi =
                request.PhysicalPixelsPerLivePixel <= 1.000001d
                && request.PreparedLive is { } prepared
                && prepared.Edges.Width > 0
                && request.LiveRoi.Width > prepared.Edges.Width
                    ? new Mat(prepared.Edges.Size(), request.LiveRoi.Type())
                    : null;
            if (inferredComputationRoi is not null)
            {
                var ratio = request.LiveRoi.Width
                    / (double)inferredComputationRoi.Width;
                request = WithComputationInput(
                    request, inferredComputationRoi, ratio);
            }
            var tuning = request.Tuning.Clone();
            tuning.Channel = request.Channel;
            tuning.Normalize();

            var savedReciprocalScale = _currentReciprocalScale;
            _currentReciprocalScale = ReciprocalScaleContext.None;
            try
            {
                if (request.PhysicalPixelsPerLivePixel <= 1.000001d
                    || request.OriginalLiveRoi is null)
                {
                    return RegisterInternal(request, tuning);
                }

                var ratio = request.PhysicalPixelsPerLivePixel;
                var totalTimer = Stopwatch.StartNew();
                var computationRequest =
                    MapStructureRequestSpace.ToComputationSpace(request, ratio);
                var coarse = RegisterInternal(computationRequest, tuning);
                if (!coarse.Accepted || coarse.Transform is null)
                {
                    totalTimer.Stop();
                    LogTwoStageCompletion(
                        request,
                        coarse,
                        coarse,
                        totalTimer.Elapsed.TotalMilliseconds,
                        originalAttemptCount: 0,
                        originalPreprocessMilliseconds: 0d,
                        executionMode: "computation-coarse-rejected");
                    return coarse;
                }

                var fineTuning = request.Tuning.Clone();
                fineTuning.Channel = request.Channel;
                fineTuning.EnableFeatureVoting = false;
                fineTuning.EnableFastAlignment = false;
                fineTuning.Normalize();
                if (request.Channel == MapAlignmentChannel.LowStructure
                    && ResolveRemainingBudget(
                        request,
                        fineTuning,
                        totalTimer) <= 0)
                {
                    totalTimer.Stop();
                    var timedOut = CreateOriginalBudgetFailure(
                        request,
                        coarse,
                        "低结构计算搜索完成后已无原图验收预算。");
                    LogTwoStageCompletion(
                        request,
                        coarse,
                        timedOut,
                        totalTimer.Elapsed.TotalMilliseconds,
                        originalAttemptCount: 0,
                        originalPreprocessMilliseconds: 0d,
                        executionMode: "computation-coarse-budget-exceeded");
                    return timedOut;
                }

                using var fineSpan = MapOperationTraceAmbient.StartChild(
                    "original_narrow_refinement",
                    MapOperationWaitKind.Compute);
                using var fineLive = _preprocessor.ProcessLiveRoi(
                    request.OriginalLiveRoi,
                    request.LiveIgnoreRegions,
                    request.DynamicIgnoreRegions,
                    generateVisibleMask: fineTuning.EnableVisibleMask,
                    profile: MapStructurePreprocessingProfile.EdgesOnly,
                    generationTuning: fineTuning.Generation);
                var remainingBudget = ResolveRemainingBudget(
                    request,
                    fineTuning,
                    totalTimer);
                if (request.Channel == MapAlignmentChannel.LowStructure
                    && remainingBudget <= 0)
                {
                    totalTimer.Stop();
                    var timedOut = CreateOriginalBudgetFailure(
                        request,
                        coarse,
                        "低结构原图预处理完成后已无原图验收预算",
                        fineLive.DiagnosticTiming?.TotalMs ?? 0d);
                    LogTwoStageCompletion(
                        request,
                        coarse,
                        timedOut,
                        totalTimer.Elapsed.TotalMilliseconds,
                        originalAttemptCount: 0,
                        originalPreprocessMilliseconds:
                            fineLive.DiagnosticTiming?.TotalMs ?? 0d,
                        executionMode: "computation-coarse-original-budget-exceeded");
                    return timedOut;
                }

                fineTuning.StructureFallbackBudgetMilliseconds = Math.Min(
                    fineTuning.StructureFallbackBudgetMilliseconds,
                    Math.Max(1, remainingBudget));
                var coarseTransform = MapStructureRequestSpace.ToPhysicalTransform(
                    coarse.Transform, ratio);
                var fine = RegisterInternal(ToOriginalFineRequest(
                    request,
                    coarseTransform,
                    fineLive), fineTuning);
                totalTimer.Stop();
                LogTwoStageCompletion(
                    request,
                    coarse,
                    fine,
                    totalTimer.Elapsed.TotalMilliseconds,
                    originalAttemptCount: 1,
                    originalPreprocessMilliseconds:
                        fineLive.DiagnosticTiming?.TotalMs ?? 0d,
                    executionMode: "computation-coarse-original-acceptance");
                return fine;
            }
            finally
            {
                _currentReciprocalScale = savedReciprocalScale;
            }
        }
    }

    private static MapStructureRegistrationRequest WithComputationInput(
        MapStructureRegistrationRequest source, Mat computationRoi, double ratio) => new()
    {
        ReferenceImage = source.ReferenceImage,
        Channel = source.Channel,
        LiveRoi = computationRoi,
        OriginalLiveRoi = source.LiveRoi,
        PhysicalPixelsPerLivePixel = ratio,
        ViewportBounds = source.ViewportBounds,
        LockedTransform = source.LockedTransform,
        Tuning = source.Tuning,
        ScaleSearchPolicy = source.ScaleSearchPolicy,
        RestrictSearchToLockedTransform = source.RestrictSearchToLockedTransform,
        TrackingMode = source.TrackingMode,
        // Computation-image metrics are only a coarse-search signal. The
        // mapped candidate must reach the strict original-pixel quality gate.
        ForceBestCandidate = source.ForceBestCandidate,
        FixedRotationDegrees = source.FixedRotationDegrees,
        ValidMapBounds = source.ValidMapBounds,
        PredictedViewportOrigin = source.PredictedViewportOrigin,
        PlayerPrior = source.PlayerPrior,
        CandidateHistory = source.CandidateHistory,
        LiveIgnoreRegions = source.LiveIgnoreRegions,
        DynamicIgnoreRegions = source.DynamicIgnoreRegions,
        DebugOutputDirectory = source.DebugOutputDirectory,
        PreparedReference = source.PreparedReference,
        PreparedLive = source.PreparedLive,
        LowStructurePlan = source.LowStructurePlan,
        SideEntrancePrior = source.SideEntrancePrior
    };

    private static MapStructureRegistrationRequest ToOriginalFineRequest(
        MapStructureRegistrationRequest source,
        MapOverlayTransform seed,
        MapStructureFeatures fineLive) => new()
    {
        ReferenceImage = source.ReferenceImage,
        Channel = source.Channel,
        LiveRoi = source.OriginalLiveRoi!,
        ViewportBounds = source.ViewportBounds,
        LockedTransform = seed,
        Tuning = source.Tuning,
        ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
        RestrictSearchToLockedTransform = true,
        TrackingMode = source.TrackingMode,
        ForceBestCandidate = false,
        FixedRotationDegrees = source.FixedRotationDegrees,
        ValidMapBounds = source.ValidMapBounds,
        PredictedViewportOrigin = source.PredictedViewportOrigin,
        PlayerPrior = source.PlayerPrior,
        CandidateHistory = [new MapSimilarityTransform
        {
            Scale = seed.ScaleX,
            RotationDegrees = seed.OrientationDegrees,
            TranslationX = seed.OffsetX,
            TranslationY = seed.OffsetY
        }],
        LiveIgnoreRegions = source.LiveIgnoreRegions,
        DynamicIgnoreRegions = source.DynamicIgnoreRegions,
        DebugOutputDirectory = source.DebugOutputDirectory,
        PreparedReference = source.PreparedReference,
        PreparedLive = fineLive,
        // Keep the route and basin metadata from the computation stage, but
        // execute original-pixel validation at exactly the selected scale.
        LowStructurePlan = ToOriginalFinePlan(source.LowStructurePlan, seed),
        SideEntrancePrior = source.SideEntrancePrior
    };

    private static LowStructureAlignmentPlan? ToOriginalFinePlan(
        LowStructureAlignmentPlan? plan,
        MapOverlayTransform seed) =>
        plan is null
            ? null
            : plan with { Scales = [seed.ScaleX] };

    private static int ResolveRemainingBudget(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning,
        Stopwatch totalTimer) =>
        request.Channel == MapAlignmentChannel.LowStructure
            ? Math.Max(
                0,
                tuning.LowStructureEndToEndBudgetMilliseconds
                - (int)Math.Ceiling(totalTimer.Elapsed.TotalMilliseconds))
            : int.MaxValue;

    private static MapStructureRegistrationResult CreateOriginalBudgetFailure(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationResult coarse,
        string detail,
        double preprocessMilliseconds = 0d) =>
        MapStructureRegistrationResult.Reject(
            MapStructureRejectionReason.TimeBudgetExceeded,
            detail,
            candidates: coarse.Candidates,
            preprocessMilliseconds:
                coarse.PreprocessMilliseconds + preprocessMilliseconds,
            searchMilliseconds: coarse.SearchMilliseconds,
            debugOutputDirectory: coarse.DebugOutputDirectory,
            lockedScale: coarse.LockedScale,
            referenceWidth: coarse.ReferenceWidth,
            referenceHeight: coarse.ReferenceHeight,
            queryEdgePixels: coarse.QueryEdgePixels,
            queryBounds: new Rect(
                coarse.QueryBoundsX,
                coarse.QueryBoundsY,
                coarse.QueryBoundsWidth,
                coarse.QueryBoundsHeight),
            scaleHypothesisCount: coarse.ScaleHypothesisCount,
            oversizedHypothesisCount: coarse.OversizedHypothesisCount,
            usedRestrictedSearch: coarse.UsedRestrictedSearch,
            lowStructureRoute: request.LowStructurePlan?.Route.ToString()
                ?? coarse.LowStructureRoute,
            lowStructureCompletedScaleCount:
                coarse.LowStructureCompletedScaleCount,
            lowStructureTranslationCandidateCount:
                coarse.LowStructureTranslationCandidateCount,
            lowStructureBudgetTerminationReason: "end-to-end-budget-exceeded",
            lowStructureVpsgEnabled: false);

    private static void LogTwoStageCompletion(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationResult computation,
        MapStructureRegistrationResult result,
        double totalMilliseconds,
        int originalAttemptCount,
        double originalPreprocessMilliseconds,
        string executionMode)
    {
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            result.Accepted ? MapLogLevel.Info : MapLogLevel.Warning,
            $"两阶段结构配准完成 · accepted={result.Accepted}",
            elapsedMs: totalMilliseconds,
            details: new()
            {
                ["executionMode"] = executionMode,
                ["computationSearchMs"] = computation.SearchMilliseconds,
                ["computationPreprocessMs"] = computation.PreprocessMilliseconds,
                ["computationScaleCount"] = computation.ScaleHypothesisCount,
                ["originalPreprocessMs"] = originalPreprocessMilliseconds,
                ["originalAcceptanceSearchMs"] =
                    originalAttemptCount == 0 ? 0d : result.SearchMilliseconds,
                ["originalScaleCount"] =
                    originalAttemptCount == 0 ? 0 : result.ScaleHypothesisCount,
                ["originalAttemptCount"] = originalAttemptCount,
                ["totalMs"] = totalMilliseconds,
                ["evidenceRoute"] = request.LowStructurePlan?.Route.ToString()
                    ?? result.LowStructureRoute,
                ["actualScale"] = result.Transform?.ScaleX,
                ["rejection"] = result.RejectionReason.ToString(),
                ["failureReason"] = result.FailureReason,
                ["budgetTerminationReason"] =
                    result.LowStructureBudgetTerminationReason
            });
    }

}
