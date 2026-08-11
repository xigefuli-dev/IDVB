namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignLockedSideEntranceFloor(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        MapAlignmentSession session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        bool tryDirectFeature = true)
    {
        if (tryDirectFeature)
        {
            var featureAttempt = _recognition.AlignLockedSideEntranceFeature(
                frame,
                locked.Map.Id,
                session,
                alignmentMode,
                tuning,
                structureTuning);
            if (featureAttempt.Recognition is not null)
                return featureAttempt;
        }

        var searchContext = CreateSideEntranceSearchContext(
            session,
            tuning,
            useInitialHighPrecisionRecovery: false);
        return _recognition.AlignSideEntrance(
            frame,
            locked.Map.Id,
            session,
            alignmentMode,
            tuning,
            structureTuning,
            alignmentSearchContext: searchContext);
    }

    private static AlignmentSearchContext CreateSideEntranceSearchContext(
        MapAlignmentSession session,
        MapRecognitionTuning tuning,
        bool useInitialHighPrecisionRecovery)
    {
        var searchContext = new AlignmentSearchContext
        {
            UseRestrictedStructureFallback = true,
            UseInitialHighPrecisionRecovery = useInitialHighPrecisionRecovery,
            GateSearch = new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = session.GateTemplateScale
                    ?? (GateTemplateRules.ReferenceScale
                        * session.BaselineGateScale),
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold =
                    GateTemplateRules.EarlyExitScoreThreshold,
                SingleGateScaleTolerance =
                    GateTemplateRules.SingleGateScaleTolerance,
                AmbiguityScoreGap =
                    GateTemplateRules.SingleGateAmbiguityGap
            }
        };
        if (tuning.WarmGateSearchBudgetMs > 0)
        {
            searchContext.GateSearch.TimeBudgetMilliseconds =
                tuning.WarmGateSearchBudgetMs;
        }
        return searchContext;
    }
}
