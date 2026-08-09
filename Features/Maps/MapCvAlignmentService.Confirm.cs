namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    internal static MapRecognitionAttempt ConfirmSelectedAlignment(
        MapCvRecognitionService service,
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
        string? mapClass = null)
    {
        var previousTransform = previousAttempt
            .Recognition?.Result.OverlayTransform;
        var previousGates = previousAttempt
            .GateDetectionResult?.Gates ?? [];

        var predictedRegions = previousGates
            .Select(g => g.ScreenBounds)
            .ToList();

        var predictedScale = previousGates.Count > 0
            ? previousGates.Average(g => g.Scale)
            : (double?)null;

        var gateContext = new GateSearchContext
        {
            Mode = GateSearchMode.LocalConfirmationSearch,
            PredictedGateRegions = predictedRegions,
            PredictedScale = predictedScale,
            LocalRoiTemplatePaddingFactor =
                tuning.ConfirmationRoiTemplatePaddingFactor,
            LocalRoiMinimumPaddingPixels =
                tuning.ConfirmationRoiMinimumPaddingPixels,
            MaximumExpectedMotionPixels =
                tuning.ConfirmationMaximumMotionPixels,
        };

        if (tuning.ConfirmationGateSearchBudgetMs > 0)
            gateContext.TimeBudgetMilliseconds =
                tuning.ConfirmationGateSearchBudgetMs;

        var alignmentContext = new AlignmentSearchContext
        {
            GateSearch = gateContext,
            PreviousAttempt = previousAttempt,
            ExpectedTransform = previousTransform is { } t
                ? MapSimilarityTransform.FromOverlay(t)
                : null,
            UseRestrictedStructureFallback = true,
            RequireCurrentFrameEvidence = true,
            AllowFullSearchUpgrade = previousGates.Count == 1,
        };

        return service.AlignSelected(
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
            alignmentSearchContext: alignmentContext,
            nativeScaleChangeRatio: nativeScaleChangeRatio,
            mapClass: mapClass);
    }
}
